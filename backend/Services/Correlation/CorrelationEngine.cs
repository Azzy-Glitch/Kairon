using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Correlation;

public interface ICorrelationEngine
{
    /// <summary>
    /// Folds detection signals into incidents (PRD section 8). Signals that belong to the same
    /// service inside the correlation window become one incident rather than five duplicates.
    /// Returns the incidents that were created or updated.
    /// </summary>
    Task<IReadOnlyList<SreIncident>> CorrelateAsync(
        IReadOnlyList<DetectionSignal> signals,
        CancellationToken cancellationToken = default);
}

public class CorrelationEngine : ICorrelationEngine
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    private readonly IIncidentKeyGenerator _keys;
    private readonly DetectionOptions _options;
    private readonly ILogger<CorrelationEngine> _logger;

    public CorrelationEngine(
        AppDbContext db,
        IAuditService audit,
        IIncidentKeyGenerator keys,
        IOptions<DetectionOptions> options,
        ILogger<CorrelationEngine> logger)
    {
        _db = db;
        _audit = audit;
        _keys = keys;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SreIncident>> CorrelateAsync(
        IReadOnlyList<DetectionSignal> signals,
        CancellationToken cancellationToken = default)
    {
        if (signals.Count == 0)
            return Array.Empty<SreIncident>();

        // A project can be deleted after detection was queued but before correlation executes.
        // Re-check here so an in-flight work item cannot resurrect an orphan incident.
        var projectIds = signals.Select(s => s.ProjectId).Distinct().ToList();
        var activeProjectIds = (await _db.Projects.AsNoTracking()
                .Where(p => projectIds.Contains(p.Id) && p.IsActive)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var eligibleSignals = signals.Where(s => activeProjectIds.Contains(s.ProjectId)).ToList();
        if (eligibleSignals.Count == 0)
            return Array.Empty<SreIncident>();

        var touched = new List<SreIncident>();

        // Find-or-create must be atomic per process: the incident worker is already a single
        // sequential consumer, but any concurrent caller must still never race two equivalent
        // incidents into existence.
        await CorrelationGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var group in eligibleSignals.GroupBy(s => s.CorrelationKey))
            {
                var grouped = group.ToList();

                // One ongoing problem = one active incident. Any non-terminal incident with the same
                // correlation key (project|environment|service[|machine]) is the same problem still
                // unfolding and absorbs the new signals, however long it has been idle: an incident
                // still awaiting approval or stuck in investigation must not be duplicated - and sent
                // to the AI again - just because its signals paused. Only a terminal state ends it; a
                // later failure then starts a genuinely new incident.
                var existing = BelongsToOngoingFault(await OpenIncidentAsync(group.Key, cancellationToken), grouped);

                // Telemetry is tied to a machine only once a remediation target is enabled for it, so
                // an incident detected before that has an unscoped key. When the fault is still
                // happening, this fresh, Agent-verified signal proves which machine it is on: the same
                // incident is re-scoped rather than a second one opened beside it.
                if (existing is null && grouped[0].MachineId is { } machineId)
                {
                    var unscoped = BelongsToOngoingFault(
                        await OpenIncidentAsync(UnscopedKey(grouped[0]), cancellationToken), grouped);
                    if (unscoped is not null)
                    {
                        Rescope(unscoped, grouped, machineId);
                        touched.Add(unscoped);
                        continue;
                    }
                }

                if (existing is null)
                {
                    var created = await CreateIncidentAsync(grouped, cancellationToken);
                    created.LastCorrelation = CorrelationOutcome.Created;
                    touched.Add(created);
                }
                else
                {
                    var before = FaultCategories.Of(SreJson.Deserialize(existing.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()));
                    var updated = await UpdateIncidentAsync(existing, grouped, cancellationToken);
                    if (updated)
                    {
                        existing.LastCorrelation = grouped.Any(s => !before.Contains(FaultCategories.Of(s.MetricName)))
                            ? CorrelationOutcome.NewSymptoms
                            : CorrelationOutcome.Updated;
                        touched.Add(existing);
                    }
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            CorrelationGate.Release();
        }
        return touched;
    }

    private static readonly SemaphoreSlim CorrelationGate = new(1, 1);

    private Task<SreIncident?> OpenIncidentAsync(string correlationKey, CancellationToken cancellationToken) =>
        _db.SreIncidents
            .Include(i => i.Events)
            .Where(i => i.CorrelationKey == correlationKey
                        && i.Status != IncidentStatus.Resolved
                        && i.Status != IncidentStatus.Failed
                        && i.Status != IncidentStatus.Rejected
                        && i.Status != IncidentStatus.Cancelled)
            .OrderByDescending(i => i.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static string UnscopedKey(DetectionSignal signal) => $"{signal.ProjectId}|{signal.Environment}|{signal.Service}";

    /// <summary>
    /// Whether these signals are part of the fault an open incident already tracks:
    ///
    /// - the same kind of fault (errors, CPU, latency, ...) is the same problem still unfolding and is
    ///   absorbed however long it paused - an incident awaiting approval is never duplicated, or sent
    ///   to the AI again, just because its signals stopped for a while;
    /// - a different kind of fault that starts while the incident's own symptoms are still being
    ///   detected is co-occurring evidence on the same service at the same time (CPU and latency of
    ///   one overload) and joins it - the AI then re-examines the combined picture once;
    /// - a different kind of fault once the incident's own symptoms are no longer being detected is a
    ///   new problem and gets its own incident immediately. There is no fixed quiet period: "no longer
    ///   detected" is measured against the detector's own re-reporting cadence (ActiveWindow).
    /// </summary>
    private SreIncident? BelongsToOngoingFault(SreIncident? incident, List<DetectionSignal> signals)
    {
        if (incident is null) return null;
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        if (snapshots.Count == 0) return incident;

        // The same fault - same kind of symptom on the same endpoint (when both are known) - resumes
        // its incident however long it paused.
        if (signals.Any(s => snapshots.Any(x => SameFault(x, s)))) return incident;

        // A different fault is co-occurring evidence only while the incident's own fault is still
        // happening. For kinds of fault the new signals cannot themselves explain, the latest samples
        // answer that directly: still over threshold -> co-occurring; measured and back to normal ->
        // stopped, so this is a new incident (no waiting for the incident's rules to age out of
        // cooldown and the evaluation window). Only when the samples cannot tell - the same kind of
        // fault on another endpoint, or a symptom metrics do not show (e.g. a log pattern) - does the
        // time since the incident's fault was last detected decide.
        var own = FaultCategories.Of(snapshots);
        own.ExceptWith(signals.Select(s => FaultCategories.Of(s.MetricName)));
        var latest = signals[0];
        if (own.Count > 0)
        {
            if (own.Overlaps(latest.BreachingNow)) return incident;
            if (own.All(latest.MeasuredNow.Contains)) return null;
        }
        var lastSeen = snapshots.Max(s => s.LastSeenAt ?? s.DetectedAt);
        return signals.Min(s => s.DetectedAt) - lastSeen <= ActiveWindow ? incident : null;
    }

    private static bool SameFault(CorrelatedSignalSnapshot existing, DetectionSignal signal) =>
        FaultCategories.Of(existing.MetricName) == FaultCategories.Of(signal.MetricName)
        && (string.IsNullOrWhiteSpace(existing.Endpoint) || string.IsNullOrWhiteSpace(signal.Endpoint)
            || string.Equals(existing.Endpoint, signal.Endpoint, StringComparison.OrdinalIgnoreCase));

    /// <summary>How long after its last detection a symptom still counts as ongoing: an ongoing breach
    /// re-fires at least once per cooldown and is evaluated over the detection window, so a symptom not
    /// seen for longer than both (twice the cooldown, to absorb one missed sweep) has stopped.</summary>
    private TimeSpan ActiveWindow => TimeSpan.FromSeconds(Math.Max(
        Math.Max(1, _options.EvaluationWindowSeconds), 2 * Math.Max(1, _options.CooldownSeconds)));

    /// <summary>Ties an unscoped incident to the machine fresh, Agent-verified telemetry proved. The
    /// unscoped readings stay in the timeline (Detected/Correlated events); the incident's actionable
    /// evidence becomes the machine-scoped signals, so its scope is never inferred from telemetry
    /// that was not proven to come from that machine.</summary>
    private void Rescope(SreIncident incident, List<DetectionSignal> signals, Guid machineId)
    {
        var previous = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        var scoped = signals.GroupBy(s => s.RuleId).Select(g => Snapshot(g.OrderByDescending(s => s.DetectedAt).First())).ToList();

        incident.CorrelationKey = signals[0].CorrelationKey;
        incident.CorrelatedMetricsJson = SreJson.Serialize(scoped);
        incident.SymptomsJson = SreJson.Serialize(scoped.Select(s => s.Symptom).ToList());
        incident.SignalCount = scoped.Count;
        incident.Title = BuildTitleFromSnapshots(scoped, incident.Service);
        incident.Severity = (IncidentSeverity)Math.Max((int)incident.Severity, (int)signals.Max(s => s.Severity));
        incident.TelemetryReferencesJson = SreJson.Serialize(
            SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>())
                .Concat(signals.SelectMany(s => s.TelemetryReferences)).Distinct().ToList());
        if (!string.IsNullOrWhiteSpace(incident.RootCause)) incident.DiagnosisStale = true;
        incident.UpdatedAt = DateTime.UtcNow;
        incident.LastCorrelation = FaultCategories.Of(previous).IsSupersetOf(scoped.Select(s => FaultCategories.Of(s.MetricName)))
            ? CorrelationOutcome.Rescoped
            : CorrelationOutcome.NewSymptoms;

        _audit.Record(incident, IncidentEventTypes.Correlated, "correlation-engine",
            message: "Tied to the remediation target's machine by fresh Agent-verified telemetry; " +
                     $"{previous.Count} earlier unscoped signal(s) kept in the timeline",
            data: new { machineId, previous, scoped });
        _logger.LogInformation("Incident {Key} re-scoped to machine {MachineId}", incident.IncidentKey, machineId);
    }

    private async Task<SreIncident> CreateIncidentAsync(
        List<DetectionSignal> signals,
        CancellationToken cancellationToken)
    {
        var primary = Dominant(signals);
        var detectedAt = signals.Min(s => s.DetectedAt);

        var incident = new SreIncident
        {
            IncidentKey = await _keys.NextIncidentKeyAsync(cancellationToken),
            ProjectId = primary.ProjectId,
            Timestamp = detectedAt,
            Application = primary.Application,
            Service = primary.Service,
            Environment = primary.Environment,
            Severity = signals.Max(s => s.Severity),
            Status = IncidentStatus.Detected,
            AffectedComponent = primary.Component,
            AffectedEndpoint = signals.Select(s => s.Endpoint).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? string.Empty,
            Title = BuildTitle(signals, primary),
            CorrelationKey = primary.CorrelationKey,
            SignalCount = signals.Count,
            SymptomsJson = SreJson.Serialize(signals.Select(s => s.Symptom).Distinct().ToList()),
            CorrelatedMetricsJson = SreJson.Serialize(signals.Select(Snapshot).ToList()),
            TelemetryReferencesJson = SreJson.Serialize(
                signals.SelectMany(s => s.TelemetryReferences).Distinct().ToList()),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SreIncidents.Add(incident);

        _audit.Record(incident, IncidentEventTypes.Detected, "detection-engine",
            newState: IncidentStatus.Detected.ToString(),
            message: $"Detected by {signals.Count} rule(s): {string.Join(", ", signals.Select(s => s.RuleId))}",
            data: signals.Select(Snapshot).ToList());

        _logger.LogInformation("Created incident {Key} ({Title}) from {Count} signal(s)",
            incident.IncidentKey, incident.Title, signals.Count);

        // Link the supporting telemetry rows so the Telemetry Monitor can show what fed the incident.
        await LinkTelemetryAsync(incident, signals, cancellationToken);

        return incident;
    }

    private async Task<bool> UpdateIncidentAsync(
        SreIncident incident,
        List<DetectionSignal> signals,
        CancellationToken cancellationToken)
    {
        var existingSnapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        var existingRefs = SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>());

        var newRuleCount = signals.Count(s => existingSnapshots.All(x => x.Rule != s.RuleId));

        // A rule re-firing with the same reading it already reported adds nothing an operator can
        // use. A rule re-firing with a genuinely different reading - or one that has not fired on
        // this incident before - is real new information, but it replaces that rule's row rather
        // than appending a duplicate. Without this, a long-running incident's Symptoms table grows
        // one row per detection sweep forever, even though the number of distinct problems never
        // changed (this was a real bug: a multi-hour incident accumulated dozens of near-identical
        // "cpu" rows, one per re-evaluation, and its "N correlated signals" count climbed the same
        // unbounded way).
        if (signals.All(s => !ReadingChanged(existingSnapshots, s)))
        {
            // Nothing new to show anyone - but the symptom IS still being detected. Record when it
            // was last seen, so "is this fault still ongoing?" reflects reality rather than the time
            // the reading last changed. Not a touch: no re-investigation, no timeline entry.
            foreach (var signal in signals)
            {
                var seen = existingSnapshots.FirstOrDefault(x => x.Rule == signal.RuleId);
                if (seen is not null) seen.LastSeenAt = signal.DetectedAt;
            }
            incident.CorrelatedMetricsJson = SreJson.Serialize(existingSnapshots);
            return false;
        }

        foreach (var signal in signals)
        {
            var snapshot = Snapshot(signal);
            var index = existingSnapshots.FindIndex(x => x.Rule == snapshot.Rule);
            if (index >= 0)
                existingSnapshots[index] = snapshot;
            else
                existingSnapshots.Add(snapshot);
        }

        existingRefs.AddRange(signals.SelectMany(s => s.TelemetryReferences));

        incident.CorrelatedMetricsJson = SreJson.Serialize(existingSnapshots);
        // Derived from the same bounded, one-row-per-rule snapshot list, so the symptom text an
        // operator reads always matches the Symptoms table exactly.
        incident.SymptomsJson = SreJson.Serialize(existingSnapshots.Select(s => s.Symptom).ToList());
        incident.TelemetryReferencesJson = SreJson.Serialize(existingRefs.Distinct().ToList());
        incident.SignalCount = existingSnapshots.Count;
        incident.UpdatedAt = DateTime.UtcNow;

        var escalated = signals.Max(s => s.Severity);
        if (escalated > incident.Severity)
        {
            _logger.LogInformation("Incident {Key} severity escalated {From} -> {To}",
                incident.IncidentKey, incident.Severity, escalated);
            incident.Severity = escalated;
        }

        if (string.IsNullOrWhiteSpace(incident.AffectedEndpoint))
        {
            incident.AffectedEndpoint =
                signals.Select(s => s.Endpoint).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? string.Empty;
        }

        // The title is regenerated because correlation may have turned a single-symptom incident
        // into a multi-symptom service degradation, which is exactly what PRD section 8 asks for.
        incident.Title = BuildTitleFromSnapshots(existingSnapshots, incident.Service);

        // An existing diagnosis was reached from less evidence than the incident now carries - either
        // a rule that has not fired before, or an existing one with a meaningfully different reading
        // - so it is flagged rather than silently left to look current. Re-investigation stays an
        // explicit operator action; correlation never rewrites a conclusion on its own. Reaching this
        // point already means at least one signal was new-or-changed (the early return above covers
        // the "nothing changed" case).
        if (!string.IsNullOrWhiteSpace(incident.RootCause))
        {
            incident.DiagnosisStale = true;

            _logger.LogInformation(
                "Incident {Key} diagnosis marked stale: {New} new rule(s), {Changed} updated reading(s) correlated in since it was produced",
                incident.IncidentKey, newRuleCount, signals.Count - newRuleCount);
        }

        _audit.Record(incident, IncidentEventTypes.Correlated, "correlation-engine",
            message: $"Correlated {signals.Count} additional signal(s): {string.Join(", ", signals.Select(s => s.RuleId))}",
            data: signals.Select(Snapshot).ToList());

        await LinkTelemetryAsync(incident, signals, cancellationToken);
        return true;
    }

    private async Task LinkTelemetryAsync(
        SreIncident incident,
        List<DetectionSignal> signals,
        CancellationToken cancellationToken)
    {
        var ids = signals.SelectMany(s => s.TelemetryReferences).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var rows = await _db.Incidents
            .Where(i => ids.Contains(i.Id) && i.SreIncidentId == null)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
            row.SreIncidentId = incident.Id;
    }

    private static DetectionSignal Dominant(List<DetectionSignal> signals) =>
        signals.OrderByDescending(s => s.Severity).ThenBy(s => s.DetectedAt).First();

    /// <summary>Whether this signal's rule is new to the incident, or its reading has moved enough
    /// to be worth recording again rather than being the same observation re-evaluated.</summary>
    private static bool ReadingChanged(List<CorrelatedSignalSnapshot> existing, DetectionSignal signal)
    {
        var match = existing.FirstOrDefault(x => x.Rule == signal.RuleId);
        if (match is null) return true;
        if (match.Observed is null || signal.Observed is null) return match.Observed != signal.Observed;
        return Math.Abs(match.Observed.Value - signal.Observed.Value) > 0.0001;
    }

    private static CorrelatedSignalSnapshot Snapshot(DetectionSignal s) => new()
    {
        Rule = s.RuleId,
        MachineId = s.MachineId,
        Endpoint = string.IsNullOrWhiteSpace(s.Endpoint) ? null : s.Endpoint,
        LastSeenAt = s.DetectedAt,
        MetricName = s.MetricName,
        Symptom = s.Symptom,
        Observed = s.Observed,
        Threshold = s.Threshold,
        Unit = s.Unit,
        Severity = s.Severity.ToString(),
        DetectedAt = s.DetectedAt
    };

    private static string BuildTitle(List<DetectionSignal> signals, DetectionSignal primary)
    {
        var metrics = signals.Select(s => s.MetricName).Distinct().ToList();
        if (metrics.Count == 1 && signals.All(s => s.RuleId.StartsWith("metric-deviation:", StringComparison.Ordinal)))
            return $"{primary.Service} {Friendly(primary.MetricName)} Anomaly";
        return $"{primary.Service} {Condition(metrics, primary.MetricName)}";
    }

    private static string BuildTitleFromSnapshots(List<CorrelatedSignalSnapshot> snapshots, string service)
    {
        var metrics = snapshots.Select(s => s.MetricName).Distinct().ToList();
        if (metrics.Count == 1 && snapshots.All(s => s.Rule.StartsWith("metric-deviation:", StringComparison.Ordinal)))
            return $"{service} {Friendly(metrics[0])} Anomaly";
        return $"{service} {Condition(metrics, metrics.FirstOrDefault() ?? string.Empty)}";
    }

    /// <summary>
    /// Turns a set of breached metrics into the phrase an operator would use. Three or more
    /// correlated metrics is the "service degradation" case from PRD section 8.
    /// </summary>
    private static string Condition(List<string> metrics, string primaryMetric)
    {
        if (metrics.Count >= 3)
            return "Service Degradation";

        if (metrics.Count == 2)
        {
            var pair = string.Join(" + ", metrics.Select(Friendly));
            return $"{pair} Anomaly";
        }

        return primaryMetric switch
        {
            "cpu" => "High CPU Utilization",
            "memory" => "High Memory Utilization",
            "latency" => "Elevated Latency",
            "errorRate" => "Elevated Error Rate",
            "errors" => "Repeated Errors",
            "retries" => "Retry Storm",
            "requests" => "Request Burst",
            "queue" => "Queue Backlog",
            "logPattern" => "Log Pattern Match",
            "processCrash" => "Process Crash",
            "processHighResource" => "Process High Resource Usage",
            _ => "Anomaly"
        };
    }

    private static string Friendly(string metric) => metric switch
    {
        "cpu" => "CPU",
        "memory" => "Memory",
        "latency" => "Latency",
        "errorRate" => "Error Rate",
        "errors" => "Errors",
        "retries" => "Retries",
        "requests" => "Requests",
        "queue" => "Queue",
        "logPattern" => "Log Pattern",
        "processCrash" => "Process Crash",
        "processHighResource" => "Process Resources",
        _ => metric
    };
}
