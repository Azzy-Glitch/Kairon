using Kairon.Backend.Configuration;
using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Platform;
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

    /// <summary>The remediation tools KAIRON can offer for this incident right now (ready, authorized
    /// target for its scope).</summary>
    Task<IReadOnlyList<string>> OfferedActionsAsync(SreIncident incident, CancellationToken cancellationToken = default);
}

public class EvidenceCollector : IEvidenceCollector
{
    private readonly AppDbContext _db;
    private readonly IRemediationToolRegistry _tools;
    private readonly AiOrchestrationOptions _options;
    private readonly DetectionOptions _detection;
    private readonly ILogger<EvidenceCollector> _logger;
    private readonly IWindowsServiceInspector _inspector;
    private readonly ILocalMachine _localMachine;

    public EvidenceCollector(
        AppDbContext db,
        IRemediationToolRegistry tools,
        IOptions<AiOrchestrationOptions> options,
        IOptions<DetectionOptions> detection,
        ILogger<EvidenceCollector> logger,
        IWindowsServiceInspector? inspector = null,
        ILocalMachine? localMachine = null)
    {
        _db = db;
        _tools = tools;
        _inspector = inspector ?? WindowsServiceInspector.Instance;
        _localMachine = localMachine ?? LocalMachine.Instance;
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

        // Two bounded slices instead of "the newest N samples": the run-up to detection, where the
        // precursors are (which signal moved first), and the latest samples (the current state). With
        // a 5 s SDK cadence the newest 30 samples cover only 150 s, so the run-up was cut off at
        // detection and lost entirely on a later re-investigation.
        var lookback = incident.Timestamp - windowStart;
        IQueryable<Metric> ScopedMetrics() => _db.Metrics.AsNoTracking()
            .Where(m => m.ProjectId == incident.ProjectId
                        && m.Environment == incident.Environment
                        && m.Service == incident.Service && (!machineId.HasValue || m.MachineId == machineId));
        var runUp = (await ScopedMetrics()
                .Where(m => m.Timestamp >= windowStart && m.Timestamp <= incident.Timestamp)
                .OrderByDescending(m => m.Timestamp).Take(MaxAnalysisSamples)
                .ToListAsync(cancellationToken))
            .OrderBy(m => m.Timestamp).Select(ToSample).ToList();
        var since = (await ScopedMetrics()
                .Where(m => m.Timestamp > incident.Timestamp)
                .OrderByDescending(m => m.Timestamp).Take(MaxAnalysisSamples)
                .ToListAsync(cancellationToken))
            .OrderBy(m => m.Timestamp).Select(ToSample).ToList();
        var latestAt = since.Count > 0 ? since[^1].Timestamp : runUp.Count > 0 ? runUp[^1].Timestamp : incident.Timestamp;
        var current = since.Where(m => m.Timestamp >= latestAt - lookback).ToList();
        var sinceBudget = current.Count == 0 ? 0 : runUp.Count == 0 ? _options.MaxMetricSamples : Math.Max(1, _options.MaxMetricSamples / 3);
        var sentMetrics = Downsample(runUp, _options.MaxMetricSamples - sinceBudget).Concat(Downsample(current, sinceBudget)).ToList();
        // Onsets are about the run-up to detection; the forecast is about where things are heading now.
        var onsetSamples = runUp.Concat(since.Where(m => m.Timestamp <= incident.Timestamp + lookback)).ToList();
        // The latest lookback-long stretch of data across both slices: right after detection that is
        // the rise itself; long after, it is purely the current state.
        var forecastSamples = runUp.Concat(since).Where(m => m.Timestamp >= latestAt - lookback).ToList();
        var evidenceSpansGap = since.Count > 0 && latestAt - incident.Timestamp > lookback;

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

        // Same project, environment and service - and, for a machine-scoped incident, the same
        // machine - so another environment's or machine's history never reaches the model.
        var history = (await _db.SreIncidents
            .AsNoTracking()
            .Include(i => i.Actions)
            .Where(i => i.Id != incident.Id
                        && i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Service == incident.Service
                        && i.RootCause != null)
            .OrderByDescending(i => i.Timestamp)
            .Take(Math.Max(1, _options.MaxHistoricalIncidents) * 4)
            .ToListAsync(cancellationToken))
            .Where(h => !machineId.HasValue || IncidentMachineScope.GetMachineId(h) == machineId)
            .Take(_options.MaxHistoricalIncidents)
            .ToList();

        // Per-endpoint totals for the exact scope and window (successes included), bounded.
        var endpointRows = await _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Service == incident.Service && (!machineId.HasValue || i.MachineId == machineId)
                        && i.Timestamp >= windowStart)
            .GroupBy(i => new { i.Endpoint, i.Method })
            .Select(g => new
            {
                g.Key.Endpoint,
                g.Key.Method,
                Requests = g.Count(),
                ServerErrors = g.Count(x => x.StatusCode >= 500),
                ClientErrors = g.Count(x => x.StatusCode >= 400 && x.StatusCode < 500),
                AvgDuration = g.Average(x => (double)x.DurationMs),
                MaxDuration = g.Max(x => x.DurationMs)
            })
            .ToListAsync(cancellationToken);
        var endpointErrorTypes = await _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Service == incident.Service && (!machineId.HasValue || i.MachineId == machineId)
                        && i.Timestamp >= windowStart && i.ErrorType != null)
            .GroupBy(i => new { i.Endpoint, i.Method, i.ErrorType })
            .Select(g => new { g.Key.Endpoint, g.Key.Method, g.Key.ErrorType, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var endpointBreakdown = endpointRows
            .OrderByDescending(e => e.ServerErrors).ThenByDescending(e => e.Requests)
            .Take(Math.Max(1, _options.MaxEndpointSummaries))
            .Select(e => new EndpointSummaryDto
            {
                Endpoint = Safe(e.Endpoint, 300) ?? string.Empty,
                Method = Safe(e.Method, 20) ?? string.Empty,
                Requests = e.Requests,
                ServerErrors = e.ServerErrors,
                ClientErrors = e.ClientErrors,
                AvgDurationMs = Math.Round(e.AvgDuration, 1),
                MaxDurationMs = e.MaxDuration,
                TopErrorType = Safe(endpointErrorTypes
                    .Where(t => t.Endpoint == e.Endpoint && t.Method == e.Method)
                    .OrderByDescending(t => t.Count).Select(t => t.ErrorType).FirstOrDefault(), 200)
            })
            .ToList();

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
        var authorizedToolNames = (await OfferedActionsAsync(incident, cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var remediationTarget = await RemediationTargetContextAsync(incident, machineId, cancellationToken);
        var recurrence = await RecurrenceAsync(incident, machineId, cancellationToken);

        var package = new EvidencePackageDto
        {
            EndpointBreakdown = endpointBreakdown,
            RemediationTarget = remediationTarget,
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
            RecentMetrics = sentMetrics,
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
                    ErrorMessage = Safe(e.ErrorMessage, 500),
                    StackExcerpt = Safe(StackExcerpt(e.StackTrace), 1200)
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

        package.Recurrence = recurrence;
        package.SignalOnsets = RiskForecaster.Onsets(onsetSamples, _detection, incident.Timestamp).Take(6).ToList();
        package.Dependencies = endpointRows
            .Where(e => string.Equals(e.Method, "DEPENDENCY", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ServerErrors).ThenByDescending(e => e.Requests)
            .Take(10)
            .Select(e => new DependencySummaryDto
            {
                Target = Safe(e.Endpoint, 300) ?? string.Empty, Calls = e.Requests, Failures = e.ServerErrors,
                AvgDurationMs = Math.Round(e.AvgDuration, 1), MaxDurationMs = e.MaxDuration
            })
            .ToList();
        package.RemediationHistory = await RemediationHistoryAsync(incident, machineId, cancellationToken);
        package.EvidenceGaps = EvidenceGaps(package, machineScoped: machineId.HasValue);
        if (evidenceSpansGap)
            package.EvidenceGaps.Insert(0, $"The incident was detected {(latestAt - incident.Timestamp).TotalMinutes:0} minute(s) before the latest sample; " +
                                           "the metrics cover the run-up to detection and the latest samples, not everything in between.");
        // Computed from the full sample set, before the payload budget may trim it, so the forecast
        // reflects everything KAIRON measured.
        package.RiskForecast = RiskForecaster.Forecast(forecastSamples, recurrence, _detection);

        EnforcePayloadBudget(package);

        _logger.LogInformation(
            "Collected evidence for {Key}: {Metrics} metric sample(s), {Errors} error(s), {Signals} signal(s), {AgentEvents} agent event(s), {History} historical",
            incident.IncidentKey, package.RecentMetrics.Count, package.RelatedErrors.Count,
            package.CorrelatedSignals.Count, package.LogEvents.Count, package.HistoricalIncidents.Count);

        return package;
    }

    public async Task<IReadOnlyList<string>> OfferedActionsAsync(SreIncident incident, CancellationToken cancellationToken = default)
    {
        var offered = new List<string>();
        foreach (var t in _tools.All())
        {
            if (t is not IScopedRemediationTool scoped || await scoped.TargetFingerprintAsync(incident, cancellationToken) is not null)
                offered.Add(t.Name);
        }
        return offered;
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

        if (package.SignalOnsets.Count > 0)
        {
            AddEvidence(incident, EvidenceKinds.SignalOnsets,
                "First to breach: " + string.Join(" -> ", package.SignalOnsets.Select(o => o.Label)), package.SignalOnsets, package.SignalOnsets.Count);
        }

        if (package.RemediationHistory.Count > 0)
        {
            AddEvidence(incident, EvidenceKinds.RemediationHistory,
                $"{package.RemediationHistory.Count} earlier remediation attempt(s)", package.RemediationHistory, package.RemediationHistory.Count);
        }

        if (package.RiskForecast is { } forecast)
        {
            AddEvidence(incident, EvidenceKinds.RiskForecast,
                $"Trend forecast: {forecast.Outcome} ({forecast.RiskLevel})", forecast, forecast.Evidence.Count);
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

    /// <summary>The single enabled target for this incident's scope, when the incident's telemetry
    /// was proven to come from that target's machine; the service state is a read-only local probe.
    /// Never includes host names, machine ids, credentials or fingerprints.</summary>
    private async Task<RemediationTargetContextDto?> RemediationTargetContextAsync(
        SreIncident incident, Guid? machineId, CancellationToken cancellationToken)
    {
        var environment = incident.Environment.ToLowerInvariant();
        var targets = await _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == incident.ProjectId && t.EnvironmentNormalized == environment && t.Service == incident.Service)
            .ToListAsync(cancellationToken);
        if (targets.Count != 1 || !machineId.HasValue || targets[0].MachineId != machineId) return null;

        var target = targets[0];
        if (target.Kind == RemediationTargetKinds.AppProcess)
        {
            // An application-process target restarts the app's own process; there is no Windows
            // service to name or probe.
            return new RemediationTargetContextDto
            {
                Kind = RemediationTargetKinds.AppProcess,
                ServiceState = "Unknown",
                TelemetryMachineScoped = true
            };
        }

        var state = "Unknown";
        try
        {
            if (_inspector.IsSupported && _localMachine.IsLocal(target.ExpectedHostName))
            {
                var probe = _inspector.Probe(target.WindowsServiceName);
                state = !probe.Exists ? "Missing" : probe.State switch
                {
                    1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 7 => "Paused", null => "Unknown", _ => "Transitioning"
                };
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            state = "Unknown";
        }

        return new RemediationTargetContextDto
        {
            Kind = RemediationTargetKinds.WindowsService,
            WindowsService = Safe(target.WindowsServiceName, 256) ?? string.Empty,
            ServiceState = state,
            TelemetryMachineScoped = true
        };
    }

    /// <summary>Restart tools whose success means "the process was restarted" - if the same problem
    /// is back soon after one of them, the restart was a temporary mitigation, not a fix.</summary>
    private static readonly string[] RestartActions =
        [ServiceToolNames.RestartApplication, ServiceToolNames.RestartService];

    /// <summary>A restart this recent counts as "the problem came back after a restart".</summary>
    internal static readonly TimeSpan RecurrenceAfterRestartWindow = TimeSpan.FromHours(1);

    /// <summary>Upper bound on samples read for analysis (onsets, forecast) per slice - never sent to the
    /// AI as such; the AI gets at most MaxMetricSamples of them.</summary>
    internal const int MaxAnalysisSamples = 300;

    private static MetricSampleDto ToSample(Metric m) => new()
    {
        Timestamp = m.Timestamp,
        CpuPercent = m.CpuPercent,
        MemoryPercent = m.MemoryPercent,
        ResponseTimeMs = m.ResponseTimeMs,
        RequestCount = m.RequestCount,
        ErrorCount = m.ErrorCount,
        RetryCount = m.RetryCount,
        QueueDepth = m.QueueDepth
    };

    /// <summary>Evenly spaced samples (always keeping the first and last), so a bounded payload still
    /// spans the whole slice instead of only its newest end.</summary>
    internal static List<MetricSampleDto> Downsample(IReadOnlyList<MetricSampleDto> samples, int count)
    {
        if (count <= 0) return new();
        if (samples.Count <= count) return samples.ToList();
        if (count == 1) return [samples[^1]];
        return Enumerable.Range(0, count)
            .Select(i => samples[(int)Math.Round(i * (samples.Count - 1) / (double)(count - 1))])
            .Distinct()
            .ToList();
    }

    /// <summary>First lines (where it was raised from) and last lines (the innermost frames and the
    /// exception line) of a stack trace - the parts that locate a failure - instead of all of it.</summary>
    internal static string? StackExcerpt(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return null;
        var lines = stackTrace.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count <= 10) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(3).Append("  ...").Concat(lines.TakeLast(6)));
    }

    /// <summary>Remediation attempts for this service (24 h) with their measured outcomes - including
    /// this incident's own: a restart whose verification failed is evidence the next diagnosis (or a
    /// re-investigation of the same incident) must not ignore.</summary>
    private async Task<List<RemediationOutcomeDto>> RemediationHistoryAsync(SreIncident incident, Guid? machineId, CancellationToken cancellationToken)
    {
        var since = incident.Timestamp.AddHours(-24);
        var incidents = (await _db.SreIncidents.AsNoTracking()
                .Include(i => i.Actions)
                .Where(i => i.ProjectId == incident.ProjectId && i.Environment == incident.Environment
                            && i.Service == incident.Service
                            && (i.Id == incident.Id || (i.Timestamp >= since && i.Timestamp <= incident.Timestamp)))
                .ToListAsync(cancellationToken))
            .Where(h => !machineId.HasValue || IncidentMachineScope.GetMachineId(h) == machineId)
            .ToList();
        var attempts = incidents.SelectMany(i => i.Actions.Select(a => (Incident: i, Action: a)))
            .Where(x => x.Action.Status is RemediationStatus.Executed or RemediationStatus.Failed)
            .OrderByDescending(x => x.Action.CompletedAt ?? x.Action.CreatedAt)
            .Take(8)
            .ToList();
        if (attempts.Count == 0) return new();

        var verificationIds = attempts.Select(x => x.Action.VerificationResultId).Where(id => id.HasValue).Select(id => id!.Value).ToList();
        var verifications = await _db.VerificationResults.AsNoTracking()
            .Where(v => verificationIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var currentKinds = FaultCategories.Of(SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()));

        return attempts.Select(x =>
        {
            var kinds = FaultCategories.Of(SreJson.Deserialize(x.Incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()));
            var verification = x.Action.VerificationResultId is { } vid && verifications.TryGetValue(vid, out var v) ? v : null;
            var at = x.Action.CompletedAt ?? x.Action.CreatedAt;
            return new RemediationOutcomeDto
            {
                IncidentKey = Safe(x.Incident.IncidentKey, 100) ?? string.Empty,
                FaultKinds = kinds.OrderBy(k => k).ToList(),
                Action = x.Action.ActionType,
                Status = x.Action.Status.ToString(),
                Verification = verification?.Status.ToString(),
                RecoveryScore = verification?.RecoveryScore,
                Error = Safe(x.Action.ExecutionError ?? verification?.FailureReason, 300),
                MinutesBeforeThisIncident = Math.Round((incident.Timestamp - at).TotalMinutes, 1),
                SameFault = x.Incident.Id == incident.Id || kinds.Overlaps(currentKinds)
            };
        }).ToList();
    }

    /// <summary>What this incident's evidence does not contain - stated, so a diagnosis can say what it
    /// could not check rather than filling the gap with a guess.</summary>
    private static List<string> EvidenceGaps(EvidencePackageDto package, bool machineScoped)
    {
        var gaps = new List<string>();
        if (package.RecentMetrics.Count < RiskForecaster.MinimumSamples)
            gaps.Add("Few metric samples in the evidence window - trends and timing are uncertain.");
        if (package.RecentMetrics.All(m => m.MemoryPercent is null))
            gaps.Add("No memory measurements.");
        var serverErrors = package.RelatedErrors.Where(e => e.StatusCode >= 500).ToList();
        if (serverErrors.Count > 0 && serverErrors.All(e => string.IsNullOrWhiteSpace(e.StackExcerpt)))
            gaps.Add("Server errors without stack traces - the failing code location is not visible.");
        if (serverErrors.Count > 0 && serverErrors.All(e => string.IsNullOrWhiteSpace(e.ErrorType) && string.IsNullOrWhiteSpace(e.ErrorMessage)))
            gaps.Add("Server errors without an exception type or message.");
        if (package.Dependencies.Count == 0)
            gaps.Add("No per-dependency call data (database or downstream API latency and failures are not reported by this app).");
        if (machineScoped)
            gaps.Add("KAIRON Agent log events are not used for this incident: they are not tied to a machine.");
        else if (package.LogEvents.Count == 0)
            gaps.Add("No application log events from the KAIRON Agent.");
        gaps.Add("No database server metrics (CPU, connections, slow queries) - KAIRON does not collect them.");
        return gaps;
    }

    private async Task<RecurrenceContextDto> RecurrenceAsync(SreIncident incident, Guid? machineId, CancellationToken cancellationToken)
    {
        var since = incident.Timestamp.AddHours(-24);
        var recent = (await _db.SreIncidents
            .AsNoTracking()
            .Include(i => i.Actions)
            .Where(i => i.Id != incident.Id
                        && i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Service == incident.Service
                        && i.Timestamp >= since
                        && i.Timestamp <= incident.Timestamp)
            .ToListAsync(cancellationToken))
            .Where(h => !machineId.HasValue || IncidentMachineScope.GetMachineId(h) == machineId)
            .ToList();

        // "It came back after a restart" only means something for the SAME kind of fault: a restart
        // for an overload says nothing about payment errors that start later.
        var currentKinds = FaultCategories.Of(SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()));
        var lastRestart = recent
            .Where(h => FaultCategories.Of(SreJson.Deserialize(h.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>())).Overlaps(currentKinds))
            .SelectMany(h => h.Actions)
            .Where(a => a.Status == RemediationStatus.Executed && a.CompletedAt.HasValue
                        && RestartActions.Contains(a.ActionType, StringComparer.Ordinal)
                        && a.CompletedAt <= incident.Timestamp
                        && incident.Timestamp - a.CompletedAt.Value <= RecurrenceAfterRestartWindow)
            .OrderByDescending(a => a.CompletedAt)
            .FirstOrDefault();

        return new RecurrenceContextDto
        {
            IncidentsLast24h = recent.Count,
            RecurredAfterRestart = lastRestart is not null,
            MinutesSinceRestart = lastRestart is null ? null : Math.Round((incident.Timestamp - lastRestart.CompletedAt!.Value).TotalMinutes, 1),
            PreviousRemediation = lastRestart?.ActionType
        };
    }

    private void EnforcePayloadBudget(EvidencePackageDto package)
    {
        var limit = Math.Max(1024, _options.MaxEvidencePayloadChars);

        // Remove least-specific context first while preserving valid structured JSON. This is an
        // aggregate wire bound, unlike truncating each persisted evidence row independently.
        while (SreJson.Serialize(package).Length > limit)
        {
            if (package.HistoricalIncidents.Count > 0) package.HistoricalIncidents.RemoveAt(0);
            else if (package.LogEvents.Count > 0) package.LogEvents.RemoveAt(0);
            else if (package.RemediationHistory.Count > 2) package.RemediationHistory.RemoveAt(package.RemediationHistory.Count - 1);
            else if (package.Dependencies.Count > 3) package.Dependencies.RemoveAt(package.Dependencies.Count - 1);
            // Stack excerpts go (oldest first, keeping the newest) before whole errors do.
            else if (package.RelatedErrors.Count(e => e.StackExcerpt is not null) > 1)
                package.RelatedErrors.First(e => e.StackExcerpt is not null).StackExcerpt = null;
            else if (package.RelatedErrors.Count > 0) package.RelatedErrors.RemoveAt(0);
            else if (package.RecentMetrics.Count > 3) package.RecentMetrics.RemoveAt(0);
            else if (package.EndpointBreakdown.Count > 1) package.EndpointBreakdown.RemoveAt(package.EndpointBreakdown.Count - 1);
            else if (package.CorrelatedSignals.Count > 1) package.CorrelatedSignals.RemoveAt(0);
            else if (package.Incident.Symptoms.Count > 1) package.Incident.Symptoms.RemoveAt(0);
            else
                throw new InvalidOperationException(
                    $"The minimum AI evidence package exceeds the configured {limit}-character limit.");
        }
    }
}
