using Kairon.Backend.Services;

namespace Kairon.Backend.Models.Sre;

/// <summary>
/// A deterministic detection result (PRD section 7). Signals are produced before any AI reasoning,
/// then folded into incidents by the correlation engine. Signals are not persisted on their own -
/// they live inside the incident they created or updated, as correlated metrics and symptoms.
/// </summary>
public class DetectionSignal
{
    public DetectionRuleKind Rule { get; set; }

    /// <summary>Stable rule id used for dedup and cooldown, e.g. "cpu-threshold".</summary>
    public string RuleId { get; set; } = string.Empty;

    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }
    public string Application { get; set; } = "Unknown";
    public string Service { get; set; } = "Unknown";
    public string Environment { get; set; } = "Development";
    public string Component { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;

    /// <summary>Short operator-facing description, e.g. "CPU 94.2% over 3 samples (threshold 80%)".</summary>
    public string Symptom { get; set; } = string.Empty;

    /// <summary>Metric name this signal is about: cpu, memory, latency, errorRate, retries, requests, queue.</summary>
    public string MetricName { get; set; } = string.Empty;

    public double? Observed { get; set; }
    public double? Threshold { get; set; }
    public string Unit { get; set; } = string.Empty;

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Telemetry rows (Incidents table) that support this signal.</summary>
    public List<Guid> TelemetryReferences { get; set; } = new();

    /// <summary>
    /// Dedup identity. Two signals with the same key inside the cooldown window are the same
    /// observation seen twice, not two separate problems.
    /// </summary>
    public string DedupKey =>
        $"{ProjectId}|{Environment}|{Service}|{Component}|{RuleId}" + (MachineId.HasValue ? $"|{MachineId}" : "");

    /// <summary>
    /// Correlation identity (PRD section 8). Signals from the same service/environment inside the
    /// correlation window fold into one incident rather than five unrelated ones.
    /// </summary>
    public string CorrelationKey =>
        $"{ProjectId}|{Environment}|{Service}" + (MachineId.HasValue ? $"|{MachineId}" : "");
}

/// <summary>Snapshot of a signal, persisted inside the incident as correlated-metric evidence.</summary>
public class CorrelatedSignalSnapshot
{
    public Guid? MachineId { get; set; }
    public string Rule { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public string Symptom { get; set; } = string.Empty;
    public double? Observed { get; set; }
    public double? Threshold { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
}

public static class IncidentMachineScope
{
    public static Guid? GetMachineId(SreIncident incident)
    {
        var signals = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        if (signals.Count == 0 || signals.Any(s => !s.MachineId.HasValue)) return null;
        var machines = signals.Select(s => s.MachineId).Distinct().ToList();
        return machines.Count == 1 ? machines[0] : null;
    }
}
