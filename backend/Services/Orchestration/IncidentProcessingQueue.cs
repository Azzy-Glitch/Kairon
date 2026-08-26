using System.Threading.Channels;
using Kairon.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Orchestration;

public enum WorkItemKind
{
    /// <summary>Run deterministic detection for a project/environment scope.</summary>
    EvaluateDetection = 0,

    /// <summary>Run AI investigation and produce recommendations for one incident.</summary>
    ProcessIncident = 1,

    /// <summary>
    /// Execute an approved remediation action and verify recovery. Split out from the approve
    /// request itself: execute (up to ExecutionTimeoutSeconds) plus verify (settle + up to
    /// MaxWaitSeconds) can take well over a minute, and running that inline on the HTTP request's
    /// own cancellation token meant a client timeout or a closed tab silently orphaned the incident
    /// in "Verifying" forever - the same reason ProcessIncident already runs off the request thread.
    /// </summary>
    ExecuteRemediation = 2
}

public record IncidentWorkItem(
    WorkItemKind Kind,
    Guid ProjectId,
    string Environment,
    string? Service = null,
    Guid? IncidentId = null,
    Guid? ActionId = null);

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
