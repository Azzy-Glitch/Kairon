using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Orchestration;

/// <summary>
/// Read side of the SRE layer. Entities are never serialized straight to the dashboard; everything
/// the frontend sees is shaped here, which is what lets storage change without breaking the UI.
/// </summary>
public interface IIncidentQueryService
{
    Task<List<SreIncidentSummaryDto>> ListAsync(
        string? status, string? severity, string? service, int limit, CancellationToken cancellationToken = default);

    Task<SreIncidentDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<IncidentEventDto>> GetTimelineAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The most recent audit events across every incident, newest first - the Overview
    /// page's activity feed (frontend PRD section 4).</summary>
    Task<List<RecentActivityEventDto>> GetRecentActivityAsync(int limit, CancellationToken cancellationToken = default);

    Task<List<IncidentEvidenceDto>> GetEvidenceAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SreDashboardDto> GetDashboardAsync(Guid? projectId, CancellationToken cancellationToken = default);

    Task<List<RemediationToolDto>> GetToolsAsync(CancellationToken cancellationToken = default);
}

public class IncidentQueryService : IIncidentQueryService
{
    private readonly AppDbContext _db;
    private readonly IRemediationToolRegistry _tools;
    private readonly IRemediationPolicy _policy;
    private readonly IAiMicroservice _ai;
    private readonly DetectionOptions _detection;
    private readonly RemediationOptions _remediation;

    public IncidentQueryService(
        AppDbContext db,
        IRemediationToolRegistry tools,
        IRemediationPolicy policy,
        IAiMicroservice ai,
        IOptions<DetectionOptions> detection,
        IOptions<RemediationOptions> remediation)
    {
        _db = db;
        _tools = tools;
        _policy = policy;
        _ai = ai;
        _detection = detection.Value;
        _remediation = remediation.Value;
    }

    public async Task<List<SreIncidentSummaryDto>> ListAsync(
        string? status,
        string? severity,
        string? service,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = RegisteredIncidents();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(i => i.Status != IncidentStatus.Resolved
                                         && i.Status != IncidentStatus.Failed
                                         && i.Status != IncidentStatus.Rejected
                                         && i.Status != IncidentStatus.Cancelled);
            }
            else if (Enum.TryParse<IncidentStatus>(status, true, out var parsed))
            {
                query = query.Where(i => i.Status == parsed);
            }
        }

        if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<IncidentSeverity>(severity, true, out var sev))
            query = query.Where(i => i.Severity == sev);

        if (!string.IsNullOrWhiteSpace(service))
            query = query.Where(i => i.Service == service);

        var incidents = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return incidents.Select(ToSummary).ToList();
    }

    public async Task<SreIncidentDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Three collection navigations in one query - EF Core's default single-query strategy
        // would JOIN all three, producing a row for every (action x event x verification)
        // combination for this one incident before EF de-duplicates it back into the object graph.
        // AsSplitQuery issues one simple query per collection instead - same result, no cartesian
        // multiplication. Safe here: this is a single AsNoTracking() read of one incident by id,
        // not a paged list, so there is no risk of the well-known split-query paging/ordering
        // pitfall (that only applies when Skip/Take is combined with a split query on the root).
        var incident = await RegisteredIncidents()
            .AsSplitQuery()
            .Include(i => i.Actions)
            .Include(i => i.Events)
            .Include(i => i.Verifications)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (incident is null)
            return null;

        var summary = ToSummary(incident);
        var verifications = incident.Verifications.Select(ToVerificationDto).ToList();

        var detail = new SreIncidentDetailDto
        {
            Id = summary.Id,
            IncidentKey = summary.IncidentKey,
            Title = summary.Title,
            Application = summary.Application,
            Service = summary.Service,
            Environment = summary.Environment,
            Severity = summary.Severity,
            Status = summary.Status,
            AffectedComponent = summary.AffectedComponent,
            AffectedEndpoint = summary.AffectedEndpoint,
            DetectedAt = summary.DetectedAt,
            UpdatedAt = summary.UpdatedAt,
            RemediationState = summary.RemediationState,
            VerificationState = summary.VerificationState,
            SignalCount = summary.SignalCount,
            Symptoms = summary.Symptoms,
            ShortDescription = summary.ShortDescription,
            NeedsApproval = summary.NeedsApproval,
            FailureReason = incident.FailureReason,
            ResolvedAt = incident.ResolvedAt,
            DiagnosisStale = incident.DiagnosisStale,
            TelemetryReferences = SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>()),
            CorrelatedSignals = SreJson
                .Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>())
                .Select(s => new CorrelatedSignalDto
                {
                    Rule = s.Rule,
                    Metric = s.MetricName,
                    Symptom = s.Symptom,
                    Observed = s.Observed,
                    Threshold = s.Threshold,
                    Unit = s.Unit,
                    Severity = s.Severity,
                    DetectedAt = s.DetectedAt
                })
                .ToList(),
            Recommendations = SreJson.Deserialize(incident.RecommendationsJson, new List<RecommendationDto>()),
            Actions = incident.Actions
                .OrderBy(a => a.CreatedAt)
                .Select(a => ToActionDto(a, verifications))
                .ToList(),
            Verifications = verifications,
            Timeline = incident.Events.OrderBy(e => e.Timestamp).Select(ToEventDto).ToList(),
            AllowedNextStates = IncidentLifecycle.NextStates(incident.Status).Select(s => s.ToString()).ToList()
        };

        // The diagnosis object only exists once the AI has actually produced one, so the UI can
        // distinguish "no diagnosis yet" from "a diagnosis with empty fields".
        if (!string.IsNullOrWhiteSpace(incident.RootCause))
        {
            var diagnosedEvent = incident.Events
                .Where(e => e.EventType == IncidentEventTypes.Diagnosed)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefault();

            var meta = SreJson.Deserialize(diagnosedEvent?.DataJson, new DiagnosisEventData());

            detail.Diagnosis = new AiDiagnosisDto
            {
                Summary = incident.Summary ?? string.Empty,
                RootCause = incident.RootCause,
                ContributingFactors = SreJson.Deserialize(incident.ContributingFactorsJson, new List<string>()),
                Evidence = meta.Evidence ?? new List<string>(),
                Confidence = incident.Confidence,
                AffectedComponents = new List<string> { incident.AffectedComponent, incident.Service }
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .ToList(),
                Provider = meta.Provider,
                Model = meta.Model,
                GeneratedAt = diagnosedEvent?.Timestamp
            };
        }

        if (!string.IsNullOrWhiteSpace(incident.PredictedImpact))
        {
            detail.Prediction = new PredictionDto
            {
                PredictedFailure = incident.PredictedImpact,
                EstimatedRisk = incident.PredictedRisk ?? "unknown",
                PotentialImpact = incident.PredictedImpact
            };
        }

        return detail;
    }

    public async Task<List<IncidentEventDto>> GetTimelineAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var events = await _db.IncidentEvents
            .AsNoTracking()
            .Where(e => e.IncidentId == id
                        && _db.SreIncidents.Any(i => i.Id == e.IncidentId
                            && _db.Projects.Any(p => p.Id == i.ProjectId && p.IsActive)))
            .OrderBy(e => e.Timestamp)
            .ToListAsync(cancellationToken);

        return events.Select(ToEventDto).ToList();
    }

    public async Task<List<RecentActivityEventDto>> GetRecentActivityAsync(int limit, CancellationToken cancellationToken = default)
    {
        var events = await _db.IncidentEvents
            .AsNoTracking()
            .Include(e => e.Incident)
            .Where(e => e.Incident != null
                        && _db.Projects.Any(p => p.Id == e.Incident.ProjectId && p.IsActive))
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);

        return events.Select(e => new RecentActivityEventDto
        {
            Id = e.Id,
            Timestamp = e.Timestamp,
            EventType = e.EventType,
            Actor = e.Actor,
            PreviousState = e.PreviousState,
            NewState = e.NewState,
            ActionId = e.ActionId,
            Result = e.Result,
            Message = e.Message,
            Error = e.Error,
            IncidentId = e.IncidentId,
            IncidentKey = e.Incident?.IncidentKey ?? string.Empty,
            IncidentTitle = e.Incident?.Title ?? string.Empty
        }).ToList();
    }

    public async Task<List<IncidentEvidenceDto>> GetEvidenceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var evidence = await _db.IncidentEvidence
            .AsNoTracking()
            .Where(e => e.IncidentId == id
                        && _db.SreIncidents.Any(i => i.Id == e.IncidentId
                            && _db.Projects.Any(p => p.Id == i.ProjectId && p.IsActive)))
            .OrderBy(e => e.CollectedAt)
            .ToListAsync(cancellationToken);

        return evidence.Select(e => new IncidentEvidenceDto
        {
            Id = e.Id,
            Kind = e.Kind,
            Summary = e.Summary,
            ItemCount = e.ItemCount,
            CollectedAt = e.CollectedAt,
            PayloadJson = e.PayloadJson
        }).ToList();
    }

    public async Task<SreDashboardDto> GetDashboardAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        var query = RegisteredIncidents();
        if (projectId.HasValue)
            query = query.Where(i => i.ProjectId == projectId.Value);

        var recent = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var open = recent.Where(i => i.IsOpen).ToList();
        var since = DateTime.UtcNow.AddHours(-24);

        var metricsQuery = _db.Metrics.AsNoTracking()
            .Where(m => _db.Projects.Any(p => p.Id == m.ProjectId && p.IsActive));
        if (projectId.HasValue)
            metricsQuery = metricsQuery.Where(m => m.ProjectId == projectId.Value);

        var recentMetrics = await metricsQuery
            .OrderByDescending(m => m.Timestamp)
            .Take(30)
            .ToListAsync(cancellationToken);

        recentMetrics.Reverse();

        var latest = recentMetrics.LastOrDefault();
        long requests = recentMetrics.Sum(m => m.RequestCount);
        long errors = recentMetrics.Sum(m => m.ErrorCount);

        var spanMinutes = recentMetrics.Count > 1
            ? Math.Max((recentMetrics[^1].Timestamp - recentMetrics[0].Timestamp).TotalMinutes, 1.0 / 60)
            : 1.0;

        var databaseHealthy = await CanConnectAsync(cancellationToken);

        return new SreDashboardDto
        {
            ActiveIncidents = open.Count,
            AwaitingApproval = open.Count(i => i.Status == IncidentStatus.AwaitingApproval),
            Remediating = open.Count(i => i.Status is IncidentStatus.Remediating or IncidentStatus.Verifying),
            ResolvedLast24h = recent.Count(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt >= since),
            ActiveServiceCount = open.Select(i => i.Service).Distinct().Count(),
            SeverityDistribution = open
                .GroupBy(i => i.Severity.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            StatusDistribution = open
                .GroupBy(i => i.Status.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            // "Most important" is severity first, then recency: an operator wants the worst thing
            // happening now, not merely the newest thing.
            TopIncident = open
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.UpdatedAt)
                .Select(ToSummary)
                .FirstOrDefault(),
            Metrics = new LiveMetricsDto
            {
                CpuPercent = latest?.CpuPercent,
                MemoryPercent = latest?.MemoryPercent,
                LatencyMs = latest?.ResponseTimeMs,
                ErrorRate = requests > 0 ? Math.Round((double)errors / requests, 3) : 0,
                RetriesPerMinute = recentMetrics.Any(m => m.RetryCount.HasValue)
                    ? Math.Round(recentMetrics.Sum(m => m.RetryCount ?? 0) / spanMinutes, 1)
                    : null,
                QueueDepth = latest?.QueueDepth,
                SampledAt = latest?.Timestamp,
                Recent = recentMetrics.Select(m => new DashboardMetricSampleDto
                {
                    Timestamp = m.Timestamp,
                    CpuPercent = m.CpuPercent,
                    MemoryPercent = m.MemoryPercent,
                    ResponseTimeMs = m.ResponseTimeMs,
                    RequestCount = m.RequestCount,
                    ErrorCount = m.ErrorCount,
                    RetryCount = m.RetryCount,
                    QueueDepth = m.QueueDepth
                }).ToList()
            },
            Health = new SystemHealthDto
            {
                Backend = true,
                Database = databaseHealthy,
                AiService = _ai.IsAvailable,
                DetectionEnabled = _detection.Enabled,
                RemediationEnabled = _remediation.Enabled,
                AiMode = await _ai.GetModeAsync(cancellationToken)
            }
        };
    }

    public async Task<List<RemediationToolDto>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        // No global authorization claim: a real incident and configured target are required.
        var probe = new SreIncident { Environment = "Production", Service = "" };

        var results = new List<RemediationToolDto>();
        foreach (var t in _tools.All())
        {
            var decision = await _policy.ValidateProposalAsync(probe, t.Name, t.RiskLevel, cancellationToken);
            results.Add(new RemediationToolDto
            {
                Name = t.Name,
                Description = t.Description,
                RiskLevel = t.RiskLevel.ToString(),
                RequiresApproval = _remediation.RequireApprovalForEveryAction,
                AllowedByPolicy = decision.Allowed,
                PolicyNote = decision.Allowed ? null : decision.Reason
            });
        }
        return results;
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            // The dashboard reporting "database: false" is far more useful than the dashboard
            // failing to render at all.
            return false;
        }
    }

    /// <summary>
    /// Autonomous views are intentionally limited to active, explicitly registered projects.
    /// Machine discovery can retain process inventory without presenting its unpaired processes
    /// as monitored applications, and orphaned history remains preserved in storage until the
    /// operator explicitly deletes it.
    /// </summary>
    private IQueryable<SreIncident> RegisteredIncidents() => _db.SreIncidents
        .AsNoTracking()
        .Where(i => _db.Projects.Any(p => p.Id == i.ProjectId && p.IsActive));

    private static SreIncidentSummaryDto ToSummary(SreIncident i)
    {
        var symptoms = SreJson.Deserialize(i.SymptomsJson, new List<string>());

        return new SreIncidentSummaryDto
        {
            Id = i.Id,
            IncidentKey = i.IncidentKey,
            Title = i.Title,
            Application = i.Application,
            Service = i.Service,
            Environment = i.Environment,
            Severity = i.Severity.ToString(),
            Status = i.Status.ToString(),
            AffectedComponent = i.AffectedComponent,
            AffectedEndpoint = i.AffectedEndpoint,
            DetectedAt = i.Timestamp,
            UpdatedAt = i.UpdatedAt,
            RemediationState = i.RemediationState.ToString(),
            VerificationState = i.VerificationState.ToString(),
            SignalCount = i.SignalCount,
            Symptoms = symptoms,
            ShortDescription = !string.IsNullOrWhiteSpace(i.Summary)
                ? i.Summary
                : symptoms.FirstOrDefault() ?? i.Title,
            NeedsApproval = i.Status == IncidentStatus.AwaitingApproval
        };
    }

    private static RemediationActionDto ToActionDto(RemediationAction a, List<VerificationResultDto> verifications) => new()
    {
        Id = a.Id,
        ActionKey = a.ActionKey,
        IncidentId = a.IncidentId,
        ActionType = a.ActionType,
        Reason = a.Reason,
        ExpectedOutcome = a.ExpectedOutcome,
        RiskLevel = a.RiskLevel.ToString(),
        RequiresApproval = a.RequiresApproval,
        Status = a.Status.ToString(),
        Source = a.Source,
        PolicyDecision = a.PolicyDecision,
        ApprovedBy = a.ApprovedBy,
        ApprovedAt = a.ApprovedAt,
        RejectedBy = a.RejectedBy,
        RejectedAt = a.RejectedAt,
        RejectionReason = a.RejectionReason,
        CreatedAt = a.CreatedAt,
        StartedAt = a.StartedAt,
        CompletedAt = a.CompletedAt,
        ExecutionResult = a.ExecutionResult,
        ExecutionError = a.ExecutionError,
        VerificationResult = verifications.FirstOrDefault(v => v.Id == a.VerificationResultId)
    };

    private static VerificationResultDto ToVerificationDto(VerificationResult v) => new()
    {
        Id = v.Id,
        ActionId = v.ActionId,
        Status = v.Status.ToString(),
        Summary = v.Summary,
        RecoveryScore = Math.Round(v.RecoveryScore, 2),
        StartedAt = v.StartedAt,
        CompletedAt = v.CompletedAt,
        FailureReason = v.FailureReason,
        Comparisons = SreJson.Deserialize(v.ComparisonsJson, new List<MetricComparison>())
            .Select(c => new MetricComparisonDto
            {
                Metric = c.Metric,
                Before = c.Before,
                After = c.After,
                Unit = c.Unit,
                Improved = c.Improved,
                MeetsThreshold = c.MeetsThreshold,
                Threshold = c.Threshold
            })
            .ToList()
    };

    private static IncidentEventDto ToEventDto(IncidentEvent e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        EventType = e.EventType,
        Actor = e.Actor,
        PreviousState = e.PreviousState,
        NewState = e.NewState,
        ActionId = e.ActionId,
        Result = e.Result,
        Message = e.Message,
        Error = e.Error
    };

    /// <summary>Shape of the JSON the orchestrator attaches to the Diagnosed audit event.</summary>
    private class DiagnosisEventData
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public List<string>? Evidence { get; set; }
        public List<string>? ContributingFactors { get; set; }
    }
}
