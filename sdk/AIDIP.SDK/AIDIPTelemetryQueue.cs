using System.Threading.Channels;
using AIDIP.SDK.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AIDIP.SDK;

/// <summary>
/// Bounded, non-blocking outbound queue for telemetry (PRD section 17).
///
/// The middleware used to fire a <c>Task.Run</c> per request, which meant a slow collector could
/// accumulate unbounded background work inside the host process. A bounded channel with a single
/// drain loop puts a hard ceiling on that: under pressure the SDK drops the oldest item and keeps
/// the application running, which is exactly the trade the PRD asks for.
/// </summary>
public interface IAIDIPTelemetryQueue
{
    /// <summary>Never blocks. Returns false when the item was dropped because the queue is full.</summary>
    bool TryEnqueue(TelemetryPayload payload);

    bool TryEnqueueMetric(MetricPayload payload);

    /// <summary>Items dropped since process start, for diagnostics.</summary>
    long DroppedCount { get; }

    int PendingCount { get; }
}

internal record TelemetryWorkItem(TelemetryPayload? Telemetry, MetricPayload? Metric);

public class AIDIPTelemetryQueue : IAIDIPTelemetryQueue
{
    private readonly Channel<TelemetryWorkItem> _channel;
    private long _dropped;

    public AIDIPTelemetryQueue(IOptions<AIDIPOptions> options)
    {
        var capacity = Math.Max(1, options.Value.QueueCapacity);

        _channel = Channel.CreateBounded<TelemetryWorkItem>(new BoundedChannelOptions(capacity)
        {
            // Dropping the oldest keeps the newest, most relevant telemetry when a collector is
            // slow, and guarantees the writer never waits.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public int PendingCount => _channel.Reader.Count;

    public bool TryEnqueue(TelemetryPayload payload) => Write(new TelemetryWorkItem(payload, null));

    public bool TryEnqueueMetric(MetricPayload payload) => Write(new TelemetryWorkItem(null, payload));

    private bool Write(TelemetryWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;

        Interlocked.Increment(ref _dropped);
        return false;
    }

    internal IAsyncEnumerable<TelemetryWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Drains the telemetry queue on a background thread. Everything it does is wrapped, because a
/// background service that throws would take the host down - the precise failure mode the SDK
/// exists to avoid.
/// </summary>
public class AIDIPTelemetrySender : BackgroundService
{
    private readonly AIDIPTelemetryQueue _queue;
    private readonly AIDIPTelemetryClient _client;

    public AIDIPTelemetrySender(IAIDIPTelemetryQueue queue, AIDIPTelemetryClient client)
    {
        // The concrete type owns the reader; the interface is what the rest of the SDK depends on.
        _queue = (AIDIPTelemetryQueue)queue;
        _client = client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (item.Telemetry is not null)
                        await _client.SendAsync(item.Telemetry, stoppingToken);
                    else if (item.Metric is not null)
                        await _client.SendMetricAsync(item.Metric, stoppingToken);
                }
                catch
                {
                    // The client already swallows its own failures; this is belt and braces so a
                    // single bad item can never stop the drain loop.
                }
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
