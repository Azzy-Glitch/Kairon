using Kairon.Backend.Configuration;
using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Evidence;

/// <summary>
/// Builds the bounded evidence package handed to the AI service (PRD section 10). The bounds are
/// the point: the AI gets focused evidence, never the database.
/// </summary>
public interface IEvidenceCollector
{
    Task<EvidencePackageDto> CollectAsync(SreIncident incident, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the collected package as IncidentEvidence rows so an operator can audit exactly
    /// what the model saw. Does not call SaveChanges.
    /// </summary>
    void Persist(SreIncident incident, EvidencePackageDto package);
}

public class EvidenceCollector : IEvidenceCollector
{
    private readonly AppDbContext _db;
    private readonly IRemediationToolRegistry _tools;
    private readonly AiOrchestrationOptions _options;
    private readonly DetectionOptions _detection;
    private readonly ILogger<EvidenceCollector> _logger;

    public EvidenceCollector(
        AppDbContext db,
        IRemediationToolRegistry tools,
        IOptions<AiOrchestrationOptions> options,
        IOptions<DetectionOptions> detection,
        ILogger<EvidenceCollector> logger)
    {
        _db = db;
        _tools = tools;
        _options = options.Value;
        _detection = detection.Value;
        _logger = logger;
    }

    public async Task<EvidencePackageDto> CollectAsync(
        SreIncident incident,
        CancellationToken cancellationToken = default)
    {
        var symptoms = SreJson.Deserialize(incident.SymptomsJson, new List<string>());
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        var machineId = IncidentMachineScope.GetMachineId(incident);
        var telemetryIds = SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>());

        // Look back a little further than detection did, so the model can see the run-up to the
        // incident rather than only the moment it tripped.
        var windowStart = incident.Timestamp.AddSeconds(-_detection.EvaluationWindowSeconds * 2);

        var metrics = await _db.Metrics
            .AsNoTracking()
            .Where(m => m.ProjectId == incident.ProjectId
                        && m.Environment == incident.Environment
                        && m.Service == incident.Service && (!machineId.HasValue || m.MachineId == machineId)
                        && m.Timestamp >= windowStart)
            .OrderByDescending(m => m.Timestamp)
            .Take(_options.MaxMetricSamples)
            .ToListAsync(cancellationToken);

        var errorsQuery = _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Service == incident.Service && (!machineId.HasValue || i.MachineId == machineId)
                        && i.Timestamp >= windowStart
                        && (i.StatusCode >= 400 || i.ErrorMessage != null));

        // Telemetry already linked to this incident is the strongest evidence there is, so it is
        // preferred over a generic time-window scan.
        var linked = await _db.Incidents
            .AsNoTracking()
            .Where(i => telemetryIds.Contains(i.Id) && i.ProjectId == incident.ProjectId && i.Service == incident.Service && i.Environment == incident.Environment && (!machineId.HasValue || i.MachineId == machineId))
            .OrderByDescending(i => i.Timestamp)
            .Take(_options.MaxRelatedErrors)
            .ToListAsync(cancellationToken);

        var errors = linked.Count >= _options.MaxRelatedErrors
            ? linked
            : linked
                .Concat(await errorsQuery
                    .OrderByDescending(i => i.Timestamp)
                    .Take(_options.MaxRelatedErrors)
                    .ToListAsync(cancellationToken))
                .DistinctBy(i => i.Id)
                .Take(_options.MaxRelatedErrors)
                .ToList();

        var history = await _db.SreIncidents
            .AsNoTracking()
            .Where(i => i.Id != incident.Id
                        && i.ProjectId == incident.ProjectId
                        && i.Service == incident.Service
                        && i.RootCause != null)
            .OrderByDescending(i => i.Timestamp)
            .Take(_options.MaxHistoricalIncidents)
            .ToListAsync(cancellationToken);

        // KAIRON Agent events (log pattern matches, process events) for the same window
        // (docs/OBSERVABILITY_MIGRATION.md). The compact CorrelatedSignals entry for these already
        // exists if a rule fired on them; this is the fuller picture - every reported event, not
        // just the ones a threshold rule turned into a signal, with the full (redacted) message.
        var agentEvents = await _db.AgentEvents
            .AsNoTracking()
            .Where(e => !machineId.HasValue && e.ProjectId == incident.ProjectId
                        && e.Environment == incident.Environment
                        && e.Service == incident.Service
                        && e.Timestamp >= windowStart)
            .OrderByDescending(e => e.Timestamp)
            .Take(_options.MaxAgentEvents)
            .ToListAsync(cancellationToken);

        // Precomputed before the (synchronous) DTO projection below can reference it - resolving a
        // scoped tool's target is a real, async EF read, so it cannot happen inside a LINQ .Where.
        var authorizedToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _tools.All())
        {
            if (t is not IScopedRemediationTool scoped || await scoped.TargetFingerprintAsync(incident, cancellationToken) is not null)
                authorizedToolNames.Add(t.Name);
        }

        var package = new EvidencePackageDto
        {
            Incident = new IncidentContextDto
            {
                IncidentId = incident.Id.ToString(),
                IncidentKey = Safe(incident.IncidentKey, 100) ?? string.Empty,
                Title = Safe(incident.Title, 300) ?? string.Empty,
                Application = Safe(incident.Application, 200) ?? string.Empty,
                Service = Safe(incident.Service, 200) ?? string.Empty,
                Environment = Safe(incident.Environment, 100) ?? string.Empty,
                Severity = incident.Severity.ToString(),
                Status = incident.Status.ToString(),
                AffectedComponent = Safe(incident.AffectedComponent, 200) ?? string.Empty,
                AffectedEndpoint = Safe(incident.AffectedEndpoint, 500) ?? string.Empty,
                DetectedAt = incident.Timestamp,
                Symptoms = symptoms.Take(20).Select(s => Safe(s, 300) ?? string.Empty).ToList()
            },
            RecentMetrics = metrics
                .OrderBy(m => m.Timestamp)
                .Select(m => new MetricSampleDto
                {
                    Timestamp = m.Timestamp,
                    CpuPercent = m.CpuPercent,
                    MemoryPercent = m.MemoryPercent,
                    ResponseTimeMs = m.ResponseTimeMs,
                    RequestCount = m.RequestCount,
                    ErrorCount = m.ErrorCount,
                    RetryCount = m.RetryCount,
                    QueueDepth = m.QueueDepth
                })
                .ToList(),
            RelatedErrors = errors
                .OrderBy(e => e.Timestamp)
                .Select(e => new RelatedErrorDto
                {
                    Timestamp = e.Timestamp,
                    Endpoint = Safe(e.Endpoint, 500) ?? string.Empty,
                    Method = Safe(e.Method, 20) ?? string.Empty,
                    StatusCode = e.StatusCode,
                    DurationMs = e.DurationMs,
                    ErrorType = Safe(e.ErrorType, 200),
                    // Error text can carry whatever the application put in an exception message,
                    // so it is scrubbed before it leaves the process.
                    ErrorMessage = Safe(e.ErrorMessage, 500)
                })
                .ToList(),
            CorrelatedSignals = snapshots.Take(30).Select(s => new CorrelatedSignalDto
            {
                Rule = Safe(s.Rule, 100) ?? string.Empty,
                Metric = Safe(s.MetricName, 100) ?? string.Empty,
                Symptom = Safe(s.Symptom, 300) ?? string.Empty,
                Observed = s.Observed,
                Threshold = s.Threshold,
                Unit = Safe(s.Unit, 30) ?? string.Empty,
                Severity = Safe(s.Severity, 30) ?? string.Empty,
                DetectedAt = s.DetectedAt
            }).ToList(),
            LogEvents = agentEvents
                .OrderBy(e => e.Timestamp)
                .Select(e => new AgentEventEvidenceDto
                {
                    Timestamp = e.Timestamp,
                    EventType = Safe(e.EventType, 100) ?? string.Empty,
                    Severity = Safe(e.Severity, 30) ?? string.Empty,
                    // Already redacted at ingestion (TelemetryController.CreateEvent) - bounded
                    // again here anyway, the same defence-in-depth every other text field in this
                    // package gets, since evidence bounds are enforced at the point evidence is
                    // built, not assumed from an upstream caller.
                    Message = Safe(e.Message, 500) ?? string.Empty,
                    Source = Safe(e.Source, 500) ?? string.Empty,
                    OccurrenceCount = e.OccurrenceCount
                })
                .ToList(),
            HistoricalIncidents = history.Select(h => new HistoricalIncidentDto
            {
                IncidentKey = Safe(h.IncidentKey, 100) ?? string.Empty,
                Title = Safe(h.Title, 300) ?? string.Empty,
                RootCause = Safe(h.RootCause, 400),
                Resolution = h.Status == IncidentStatus.Resolved
                    ? Safe(h.Actions.Select(a => a.ActionType).FirstOrDefault(), 100)
                    : null,
                DetectedAt = h.Timestamp,
                Status = h.Status.ToString()
            }).ToList(),
            // The closed set of things the model is allowed to propose. Anything it invents
            // outside this list is rejected by policy before it can reach an executor.
            AvailableActions = _tools.All()
                .Where(t => authorizedToolNames.Contains(t.Name))
                .Select(t => new AvailableActionDto
                {
                    Action = t.Name,
                    Description = t.Description,
                    RiskLevel = t.RiskLevel.ToString()
                })
                .ToList()
        };

        EnforcePayloadBudget(package);

        _logger.LogInformation(
            "Collected evidence for {Key}: {Metrics} metric sample(s), {Errors} error(s), {Signals} signal(s), {AgentEvents} agent event(s), {History} historical",
            incident.IncidentKey, package.RecentMetrics.Count, package.RelatedErrors.Count,
            package.CorrelatedSignals.Count, package.LogEvents.Count, package.HistoricalIncidents.Count);

        return package;
    }

    public void Persist(SreIncident incident, EvidencePackageDto package)
    {
        AddEvidence(incident, EvidenceKinds.IncidentMetadata,
            $"{incident.Title} ({incident.Severity})", package.Incident, package.Incident.Symptoms.Count);

        AddEvidence(incident, EvidenceKinds.RecentMetrics,
            $"{package.RecentMetrics.Count} metric sample(s)", package.RecentMetrics, package.RecentMetrics.Count);

        AddEvidence(incident, EvidenceKinds.RelatedErrors,
            $"{package.RelatedErrors.Count} related error(s)", package.RelatedErrors, package.RelatedErrors.Count);

        AddEvidence(incident, EvidenceKinds.CorrelatedSignals,
            $"{package.CorrelatedSignals.Count} correlated signal(s)", package.CorrelatedSignals, package.CorrelatedSignals.Count);

        if (package.LogEvents.Count > 0)
        {
            AddEvidence(incident, EvidenceKinds.AgentEvents,
                $"{package.LogEvents.Count} Agent event(s)", package.LogEvents, package.LogEvents.Count);
        }

        if (package.HistoricalIncidents.Count > 0)
        {
            AddEvidence(incident, EvidenceKinds.HistoricalIncidents,
                $"{package.HistoricalIncidents.Count} similar past incident(s)",
                package.HistoricalIncidents, package.HistoricalIncidents.Count);
        }
    }

    private void AddEvidence(SreIncident incident, string kind, string summary, object payload, int count)
    {
        var evidence = new IncidentEvidence
        {
            IncidentId = incident.Id,
            Kind = kind,
            Summary = summary,
            ItemCount = count,
            PayloadJson = SreJson.Truncate(SreJson.Serialize(payload), _options.MaxEvidencePayloadChars),
            CollectedAt = DateTime.UtcNow
        };

        incident.Evidence.Add(evidence);
        _db.IncidentEvidence.Add(evidence);
    }

    private static string? Bound(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "...";

    private static string? Safe(string? value, int max) =>
        Bound(Audit.Redaction.Scrub(value), max);

    private void EnforcePayloadBudget(EvidencePackageDto package)
    {
        var limit = Math.Max(1024, _options.MaxEvidencePayloadChars);

        // Remove least-specific context first while preserving valid structured JSON. This is an
        // aggregate wire bound, unlike truncating each persisted evidence row independently.
        while (SreJson.Serialize(package).Length > limit)
        {
            if (package.HistoricalIncidents.Count > 0) package.HistoricalIncidents.RemoveAt(0);
            else if (package.LogEvents.Count > 0) package.LogEvents.RemoveAt(0);
            else if (package.RelatedErrors.Count > 0) package.RelatedErrors.RemoveAt(0);
            else if (package.RecentMetrics.Count > 3) package.RecentMetrics.RemoveAt(0);
            else if (package.CorrelatedSignals.Count > 1) package.CorrelatedSignals.RemoveAt(0);
            else if (package.Incident.Symptoms.Count > 1) package.Incident.Symptoms.RemoveAt(0);
            else
                throw new InvalidOperationException(
                    $"The minimum AI evidence package exceeds the configured {limit}-character limit.");
        }
    }
}
