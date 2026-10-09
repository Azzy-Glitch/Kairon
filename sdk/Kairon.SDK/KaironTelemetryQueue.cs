using System.Threading.Channels;
using Kairon.SDK.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kairon.SDK;

/// <summary>
/// Bounded, non-blocking outbound queue for telemetry (PRD section 17).
///
/// The middleware used to fire a <c>Task.Run</c> per request, which meant a slow collector could
/// accumulate unbounded background work inside the host process. A bounded channel with a single
/// drain loop puts a hard ceiling on that: under pressure the SDK drops the oldest item and keeps
/// the application running, which is exactly the trade the PRD asks for.
/// </summary>
public interface IKaironTelemetryQueue
{
    /// <summary>Never blocks. Returns false when the item was dropped because the queue is full.</summary>
    bool TryEnqueue(TelemetryPayload payload);

    bool TryEnqueueMetric(MetricPayload payload);

    /// <summary>Items dropped since process start, for diagnostics.</summary>
    long DroppedCount { get; }

    int PendingCount { get; }
    long DeliveredCount { get; }
    long FailedCount { get; }

    /// <summary>The most recent delivery failure's message (e.g. "Kairon server returned 401.
    /// (project authentication rejected)"), or null once a delivery has since succeeded. Never
    /// contains the API key or any other secret. Mirrors sdk-python's `last_delivery_error` - a
    /// safe diagnostic, not an action trigger: nothing reads this to decide whether to re-pair.</summary>
    string? LastDeliveryError { get; }

    /// <summary>Includes queued and in-flight items; false on timeout or any lifetime loss.</summary>
    Task<bool> FlushAsync(CancellationToken cancellationToken = default);
}

internal record TelemetryWorkItem(TelemetryPayload? Telemetry, MetricPayload? Metric);

public class KaironTelemetryQueue : IKaironTelemetryQueue
{
    private readonly Channel<TelemetryWorkItem> _channel;
    private long _dropped, _delivered, _failed;
    private readonly object _gate = new();
    private int _outstanding;
    private TaskCompletionSource _idle = Completed();
    private static TaskCompletionSource Completed() {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(); return source;
    }

    public KaironTelemetryQueue(IOptions<KaironOptions> options)
    {
        var capacity = Math.Max(1, options.Value.QueueCapacity);

        _channel = Channel.CreateBounded<TelemetryWorkItem>(new BoundedChannelOptions(capacity)
        {
            // Dropping the oldest keeps the newest, most relevant telemetry when a collector is
            // slow, and guarantees the writer never waits.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        }, _ => { Interlocked.Increment(ref _dropped); Finish(); });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public int PendingCount => _channel.Reader.Count;
    public long DeliveredCount => Interlocked.Read(ref _delivered);
    public long FailedCount => Interlocked.Read(ref _failed);
    public string? LastDeliveryError => Volatile.Read(ref _lastDeliveryError);
    private string? _lastDeliveryError;

    internal void Complete() => _channel.Writer.TryComplete();
    internal void RecordDelivery(bool delivered, string? error = null) {
        if (delivered) { Interlocked.Increment(ref _delivered); Volatile.Write(ref _lastDeliveryError, null); }
        else { Interlocked.Increment(ref _failed); Volatile.Write(ref _lastDeliveryError, error); }
        Finish();
    }
    private void Finish() {
        lock (_gate) { if (--_outstanding == 0) _idle.TrySetResult(); }
    }
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default) {
        Task idle;
        lock (_gate) idle = _idle.Task;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await idle.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) { return false; }
        return FailedCount == 0 && DroppedCount == 0;
    }

    public bool TryEnqueue(TelemetryPayload payload) => Write(new TelemetryWorkItem(payload, null));

    public bool TryEnqueueMetric(MetricPayload payload) => Write(new TelemetryWorkItem(null, payload));

    private bool Write(TelemetryWorkItem item)
    {
        lock (_gate) {
            if (_outstanding++ == 0)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_channel.Writer.TryWrite(item)) return true;
            Interlocked.Increment(ref _dropped);
            Finish();
            return false;
        }
    }

    internal IAsyncEnumerable<TelemetryWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>Takes whatever is already queued, up to <paramref name="max"/>, without waiting -
    /// batches grow naturally while a send is in flight and never add latency when idle.</summary>
    internal void DrainAvailable(List<TelemetryWorkItem> into, int max)
    {
        while (into.Count < max && _channel.Reader.TryRead(out var item)) into.Add(item);
    }
}

/// <summary>
/// Drains the telemetry queue on a background thread. Everything it does is wrapped, because a
/// background service that throws would take the host down - the precise failure mode the SDK
/// exists to avoid.
/// </summary>
public class KaironTelemetrySender : BackgroundService
{
    /// <summary>Events per POST. Well inside the backend's 200-event/1 MiB batch bound, and small
    /// enough that one rejected or timed-out batch never costs much telemetry.</summary>
    internal const int MaxBatchEvents = 25;

    private readonly KaironTelemetryQueue _queue;
    private readonly KaironTelemetryClient _client;
    private readonly KaironOptions _options;
    private volatile bool _stopping;

    public KaironTelemetrySender(IKaironTelemetryQueue queue, KaironTelemetryClient client)
    {
        // The concrete type owns the reader; the interface is what the rest of the SDK depends on.
        _queue = (KaironTelemetryQueue)queue;
        _client = client;
        _options = client.Options;
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        // Shutdown drain is bounded below; a rate-limit pause must not eat into it.
        _stopping = true;
        _queue.Complete();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ShutdownTimeoutSeconds, 1, 60)));
        try {
            if (ExecuteTask is not null) await ExecuteTask.WaitAsync(deadline.Token);
        } catch (OperationCanceledException) { }
        // Cancel an in-flight HTTP request if the bounded drain expired.
        await base.StopAsync(deadline.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var batch = new List<TelemetryWorkItem>(MaxBatchEvents);
            while (await _queue.WaitToReadAsync(stoppingToken))
            {
                batch.Clear();
                _queue.DrainAvailable(batch, MaxBatchEvents);
                if (batch.Count > 0) await DeliverAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Never propagate: an SDK fault must not fault the host.
        }
    }

    /// <summary>
    /// Delivers one drained batch, split by machine-proof scope so each part can still carry the
    /// Agent's proof (one application normally has a single scope, so this is normally one POST).
    /// Every item is recorded exactly once - delivered or failed - whatever happens, because
    /// FlushAsync waits on that count.
    /// </summary>
    private async Task DeliverAsync(List<TelemetryWorkItem> batch, CancellationToken stoppingToken)
    {
        var recorded = 0;
        try
        {
            var groups = new List<(NormalizedBatchScope Scope, List<NormalizedTelemetryEvent> Events)>();
            foreach (var item in batch)
            {
                NormalizedTelemetryEvent normalized;
                try {
                    normalized = item.Telemetry is not null
                        ? NormalizedTelemetryEvent.From(item.Telemetry, _options)
                        : NormalizedTelemetryEvent.From(item.Metric!, _options);
                } catch {
                    _queue.RecordDelivery(false, "Transport or serialization failure");
                    recorded++;
                    continue;
                }
                var scope = NormalizedBatchScope.Of(normalized);
                var index = groups.FindIndex(g => g.Scope == scope);
                if (index < 0) groups.Add((scope, new List<NormalizedTelemetryEvent> { normalized }));
                else groups[index].Events.Add(normalized);
            }

            foreach (var (_, events) in groups)
            {
                var result = await _client.SendNormalizedBatchAsync(events, stoppingToken);
                if (result.RateLimited && !_stopping)
                {
                    // Back off for the server's window instead of hammering it, then resend the
                    // same events (same EventIds, so a commit we never heard about is a duplicate).
                    await Task.Delay(result.RetryAfter!.Value, stoppingToken);
                    result = await _client.SendNormalizedBatchAsync(events, stoppingToken);
                }
                for (var i = 0; i < events.Count; i++)
                {
                    var delivered = i < result.Delivered;
                    _queue.RecordDelivery(delivered, delivered ? null : result.Message);
                    recorded++;
                }
            }
        }
        catch { /* Fail open; anything unrecorded is counted as failed below. */ }
        finally
        {
            for (; recorded < batch.Count; recorded++)
                _queue.RecordDelivery(false, "Transport or serialization failure");
        }
    }
}
