namespace Kairon.Backend.Models.Sre;

/// <summary>
/// Post-remediation verification against fresh telemetry (backend PRD section 13, frontend PRD
/// section 12). The backend is authoritative: only a Passed verification lets an incident reach
/// Resolved.
/// </summary>
public class VerificationResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public SreIncident? Incident { get; set; }

    public Guid? ActionId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;

    public string Summary { get; set; } = string.Empty;

    /// <summary>Serialized List of MetricComparison - before/after per metric.</summary>
    public string ComparisonsJson { get; set; } = "[]";

    /// <summary>Fraction of checked metrics that recovered, 0..1.</summary>
    public double RecoveryScore { get; set; }

    public string? FailureReason { get; set; }
}

/// <summary>A single before/after metric comparison used by the verification UI.</summary>
public class MetricComparison
{
    public string Metric { get; set; } = string.Empty;
    public double? Before { get; set; }
    public double? After { get; set; }
    public string Unit { get; set; } = string.Empty;
    public bool Improved { get; set; }
    public bool MeetsThreshold { get; set; }
    public double? Threshold { get; set; }
}
