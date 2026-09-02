using System.Collections.Concurrent;
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
    ExecuteRemediation = 2,

    /// <summary>
    /// Explicit operator-requested re-investigation. Kept distinct from automatic first-pass
    /// processing so duplicate detection work can never turn into repeated AI calls after an
    /// incident has already advanced to AwaitingApproval.
    /// </summary>
    ReinvestigateIncident = 3
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

    /// <summary>
    /// Releases the de-duplication lease for an item after processing finishes. Consumers must
    /// call this in a finally block so failed work can be requested again deliberately.
    /// </summary>
    void Complete(IncidentWorkItem item);

    IAsyncEnumerable<IncidentWorkItem> ReadAllAsync(CancellationToken cancellationToken);

    int Count { get; }
}

public class IncidentProcessingQueue : IIncidentProcessingQueue
{
    private readonly Channel<IncidentWorkItem> _channel;
    private readonly ConcurrentDictionary<WorkItemKey, byte> _active = new();
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
            // Wait mode plus TryWrite gives us an observable, non-blocking rejection when full.
            // DropWrite reports a successful TryWrite even when it discards the item, which would
            // leave the item's de-duplication lease permanently held.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(IncidentWorkItem item)
    {
        var key = WorkItemKey.From(item);

        // Returning true means the requested work is already scheduled or running. This keeps
        // ingestion non-blocking without presenting harmless coalescing as a queue failure.
        if (!_active.TryAdd(key, 0))
        {
            _logger.LogDebug(
                "Coalesced duplicate {Kind} work item for project {ProjectId}",
                item.Kind, item.ProjectId);
            return true;
        }

        if (_channel.Writer.TryWrite(item))
            return true;

        _active.TryRemove(key, out _);

        var dropped = Interlocked.Increment(ref _dropped);

        // Logged at warning because a persistently full queue is a real capacity signal, but it is
        // never allowed to become an exception on the ingestion path.
        _logger.LogWarning(
            "Incident processing queue is full; dropped {Kind} for project {ProjectId} ({Dropped} dropped so far)",
            item.Kind, item.ProjectId, dropped);

        return false;
    }

    public void Complete(IncidentWorkItem item) =>
        _active.TryRemove(WorkItemKey.From(item), out _);

    public IAsyncEnumerable<IncidentWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    private readonly record struct WorkItemKey(
        string Category,
        Guid ProjectId,
        string Environment,
        string Service,
        Guid TargetId)
    {
        public static WorkItemKey From(IncidentWorkItem item)
        {
            var environment = item.Environment.Trim().ToUpperInvariant();
            var service = item.Service?.Trim().ToUpperInvariant() ?? string.Empty;

            return item.Kind switch
            {
                WorkItemKind.EvaluateDetection =>
                    new("detection", item.ProjectId, environment, service, Guid.Empty),

                // Initial and operator-requested investigation share one lease: two model calls
                // for the same incident must never overlap or wait back-to-back in the channel.
                WorkItemKind.ProcessIncident or WorkItemKind.ReinvestigateIncident =>
                    new("investigation", Guid.Empty, string.Empty, string.Empty,
                        item.IncidentId ?? Guid.Empty),

                WorkItemKind.ExecuteRemediation =>
                    new("remediation", Guid.Empty, string.Empty, string.Empty,
                        item.ActionId ?? item.IncidentId ?? Guid.Empty),

                _ => new(item.Kind.ToString(), item.ProjectId, environment, service,
                    item.IncidentId ?? item.ActionId ?? Guid.Empty)
            };
        }
    }
}
