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

        var touched = new List<SreIncident>();
        var window = TimeSpan.FromSeconds(_options.CorrelationWindowSeconds);

        foreach (var group in signals.GroupBy(s => s.CorrelationKey))
        {
            var grouped = group.ToList();
            var cutoff = grouped.Max(s => s.DetectedAt) - window;

            // An open incident with the same correlation key inside the window is the same
            // problem still unfolding, so new signals attach to it instead of spawning a twin.
            var existing = await _db.SreIncidents
                .Include(i => i.Events)
                .Where(i => i.CorrelationKey == group.Key
                            && i.Status != IncidentStatus.Resolved
                            && i.Status != IncidentStatus.Failed
                            && i.Status != IncidentStatus.Rejected
                            && i.Status != IncidentStatus.Cancelled
                            && i.UpdatedAt >= cutoff)
                .OrderByDescending(i => i.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
            {
                var created = await CreateIncidentAsync(grouped, cancellationToken);
                touched.Add(created);
            }
            else
            {
                var updated = await UpdateIncidentAsync(existing, grouped, cancellationToken);
                if (updated)
                    touched.Add(existing);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return touched;
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
        var existingSymptoms = SreJson.Deserialize(incident.SymptomsJson, new List<string>());
        var existingSnapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        var existingRefs = SreJson.Deserialize(incident.TelemetryReferencesJson, new List<Guid>());

        var newSymptoms = signals.Select(s => s.Symptom).Where(s => !existingSymptoms.Contains(s)).ToList();

        // Re-firing the same rule with the same numbers adds nothing an operator can use.
        if (newSymptoms.Count == 0 && signals.All(s => existingSnapshots.Any(x => x.Symptom == s.Symptom)))
            return false;

        existingSymptoms.AddRange(newSymptoms);
        existingSnapshots.AddRange(signals.Select(Snapshot));
        existingRefs.AddRange(signals.SelectMany(s => s.TelemetryReferences));

        incident.SymptomsJson = SreJson.Serialize(existingSymptoms.Distinct().ToList());
        incident.CorrelatedMetricsJson = SreJson.Serialize(existingSnapshots);
        incident.TelemetryReferencesJson = SreJson.Serialize(existingRefs.Distinct().ToList());
        incident.SignalCount += signals.Count;
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

        // An existing diagnosis was reached from less evidence than the incident now carries, so it
        // is flagged rather than silently left to look current. Re-investigation stays an explicit
        // operator action; correlation never rewrites a conclusion on its own.
        if (!string.IsNullOrWhiteSpace(incident.RootCause) && newSymptoms.Count > 0)
        {
            incident.DiagnosisStale = true;

            _logger.LogInformation(
                "Incident {Key} diagnosis marked stale: {Count} new symptom(s) correlated in since it was produced",
                incident.IncidentKey, newSymptoms.Count);
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

    private static CorrelatedSignalSnapshot Snapshot(DetectionSignal s) => new()
    {
        Rule = s.RuleId,
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
        return $"{primary.Service} {Condition(metrics, primary.MetricName)}";
    }

    private static string BuildTitleFromSnapshots(List<CorrelatedSignalSnapshot> snapshots, string service)
    {
        var metrics = snapshots.Select(s => s.MetricName).Distinct().ToList();
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
        _ => metric
    };
}
