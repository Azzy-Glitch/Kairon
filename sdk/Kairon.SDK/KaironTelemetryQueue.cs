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
}

/// <summary>
/// Drains the telemetry queue on a background thread. Everything it does is wrapped, because a
/// background service that throws would take the host down - the precise failure mode the SDK
/// exists to avoid.
/// </summary>
public class KaironTelemetrySender : BackgroundService
{
    private readonly KaironTelemetryQueue _queue;
    private readonly KaironTelemetryClient _client;

    public KaironTelemetrySender(IKaironTelemetryQueue queue, KaironTelemetryClient client)
    {
        // The concrete type owns the reader; the interface is what the rest of the SDK depends on.
        _queue = (KaironTelemetryQueue)queue;
        _client = client;
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        _queue.Complete();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
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
            await foreach (var item in _queue.ReadAllAsync(stoppingToken))
            {
                var delivered = false;
                string? error = null;
                try {
                    var result = item.Telemetry is not null
                        ? await _client.SendAsync(item.Telemetry, stoppingToken)
                        : await _client.SendMetricAsync(item.Metric!, stoppingToken);
                    delivered = result?.Success == true;
                    if (!delivered) error = result?.Message;
                } catch { error = "Transport or serialization failure"; /* Fail open; loss remains visible in FailedCount. */ }
                finally { _queue.RecordDelivery(delivered, error); }
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
}
