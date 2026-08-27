using Kairon.Backend.Configuration;
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
        var telemetryIds = SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>());

        // Look back a little further than detection did, so the model can see the run-up to the
        // incident rather than only the moment it tripped.
        var windowStart = incident.Timestamp.AddSeconds(-_detection.EvaluationWindowSeconds * 2);

        var metrics = await _db.Metrics
            .AsNoTracking()
            .Where(m => m.ProjectId == incident.ProjectId
                        && m.Environment == incident.Environment
                        && m.Timestamp >= windowStart)
            .OrderByDescending(m => m.Timestamp)
            .Take(_options.MaxMetricSamples)
            .ToListAsync(cancellationToken);

        var errorsQuery = _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Timestamp >= windowStart
                        && (i.StatusCode >= 400 || i.ErrorMessage != null));

        // Telemetry already linked to this incident is the strongest evidence there is, so it is
        // preferred over a generic time-window scan.
        var linked = await _db.Incidents
            .AsNoTracking()
            .Where(i => telemetryIds.Contains(i.Id))
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
            .Where(e => e.ProjectId == incident.ProjectId
                        && e.Environment == incident.Environment
                        && e.Service == incident.Service
                        && e.Timestamp >= windowStart)
            .OrderByDescending(e => e.Timestamp)
            .Take(_options.MaxAgentEvents)
            .ToListAsync(cancellationToken);

        var package = new EvidencePackageDto
        {
            Incident = new IncidentContextDto
            {
                IncidentId = incident.Id.ToString(),
                IncidentKey = incident.IncidentKey,
                Title = incident.Title,
                Application = incident.Application,
                Service = incident.Service,
                Environment = incident.Environment,
                Severity = incident.Severity.ToString(),
                Status = incident.Status.ToString(),
                AffectedComponent = incident.AffectedComponent,
                AffectedEndpoint = incident.AffectedEndpoint,
                DetectedAt = incident.Timestamp,
                Symptoms = symptoms
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
                    Endpoint = e.Endpoint,
                    Method = e.Method,
                    StatusCode = e.StatusCode,
                    DurationMs = e.DurationMs,
                    ErrorType = e.ErrorType,
                    // Error text can carry whatever the application put in an exception message,
                    // so it is scrubbed before it leaves the process.
                    ErrorMessage = Audit.Redaction.Scrub(Bound(e.ErrorMessage, 500))
                })
                .ToList(),
            CorrelatedSignals = snapshots.Select(s => new CorrelatedSignalDto
            {
                Rule = s.Rule,
                Metric = s.MetricName,
                Symptom = s.Symptom,
                Observed = s.Observed,
                Threshold = s.Threshold,
                Unit = s.Unit,
                Severity = s.Severity,
                DetectedAt = s.DetectedAt
            }).ToList(),
            LogEvents = agentEvents
                .OrderBy(e => e.Timestamp)
                .Select(e => new AgentEventEvidenceDto
                {
                    Timestamp = e.Timestamp,
                    EventType = e.EventType,
                    Severity = e.Severity,
                    // Already redacted at ingestion (TelemetryController.CreateEvent) - bounded
                    // again here anyway, the same defence-in-depth every other text field in this
                    // package gets, since evidence bounds are enforced at the point evidence is
                    // built, not assumed from an upstream caller.
                    Message = Bound(e.Message, 500) ?? string.Empty,
                    Source = e.Source,
                    OccurrenceCount = e.OccurrenceCount
                })
                .ToList(),
            HistoricalIncidents = history.Select(h => new HistoricalIncidentDto
            {
                IncidentKey = h.IncidentKey,
                Title = h.Title,
                RootCause = Bound(h.RootCause, 400),
                Resolution = h.Status == IncidentStatus.Resolved
                    ? h.Actions.Select(a => a.ActionType).FirstOrDefault()
                    : null,
                DetectedAt = h.Timestamp,
                Status = h.Status.ToString()
            }).ToList(),
            // The closed set of things the model is allowed to propose. Anything it invents
            // outside this list is rejected by policy before it can reach an executor.
            AvailableActions = _tools.All()
                .Select(t => new AvailableActionDto
                {
                    Action = t.Name,
                    Description = t.Description,
                    RiskLevel = t.RiskLevel.ToString()
                })
                .ToList()
        };

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
}
