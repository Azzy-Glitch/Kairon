using System.Threading.Channels;
using Kairon.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Orchestration;

public enum WorkItemKind
{
    /// <summary>Run deterministic detection for a project/environment scope.</summary>
    EvaluateDetection = 0,

    /// <summary>Run AI investigation and produce recommendations for one incident.</summary>
    ProcessIncident = 1
}

public record IncidentWorkItem(
    WorkItemKind Kind,
    Guid ProjectId,
    string Environment,
    string? Service = null,
    Guid? IncidentId = null);

/// <summary>
/// The seam that keeps AI off the telemetry ingestion path (PRD section 6: "AI calls must never
/// block the primary telemetry ingestion path").
///
/// Deliberately an in-process bounded channel rather than Kafka or Redis: PRD section 21 forbids
/// introducing distributed message infrastructure without demonstrated need, and a single backend
/// instance has none.
/// </summary>
public interface IIncidentProcessingQueue
{
    /// <summary>
    /// Non-blocking enqueue. Returns false when the queue is full - ingestion drops the analysis
    /// rather than slowing down, because losing an evaluation is survivable and blocking ingestion
    /// is not.
    /// </summary>
    bool TryEnqueue(IncidentWorkItem item);

    IAsyncEnumerable<IncidentWorkItem> ReadAllAsync(CancellationToken cancellationToken);

    int Count { get; }
}

public class IncidentProcessingQueue : IIncidentProcessingQueue
{
    private readonly Channel<IncidentWorkItem> _channel;
    private readonly ILogger<IncidentProcessingQueue> _logger;
    private int _dropped;

    public IncidentProcessingQueue(
        IOptions<AiOrchestrationOptions> options,
        ILogger<IncidentProcessingQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<IncidentWorkItem>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            // Never block a producer: the producer is usually an HTTP request thread handling
            // telemetry, and telemetry ingestion has to stay fast.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(IncidentWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;

        var dropped = Interlocked.Increment(ref _dropped);

        // Logged at warning because a persistently full queue is a real capacity signal, but it is
        // never allowed to become an exception on the ingestion path.
        _logger.LogWarning(
            "Incident processing queue is full; dropped {Kind} for project {ProjectId} ({Dropped} dropped so far)",
            item.Kind, item.ProjectId, dropped);

        return false;
    }

    public IAsyncEnumerable<IncidentWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
