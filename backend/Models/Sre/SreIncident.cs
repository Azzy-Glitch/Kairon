namespace Kairon.Backend.Models.Sre;

/// <summary>
/// The Kairon Autonomous AI SRE incident aggregate (PRD section 5).
///
/// This is deliberately a different concept from <see cref="Kairon.Backend.Models.Incident"/>:
/// that type is a single request-scoped telemetry record (one failed HTTP call), and it stays
/// exactly as it is so the SDK contract and existing frontend screens keep working. An
/// <see cref="SreIncident"/> aggregates many of those telemetry rows plus correlated metrics
/// into one operator-facing incident with a lifecycle. Telemetry rows are referenced through
/// <see cref="TelemetryReferencesJson"/> rather than copied.
/// </summary>
public class SreIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-facing short key, e.g. INC-0001.</summary>
    public string IncidentKey { get; set; } = string.Empty;

    public Guid ProjectId { get; set; }

    /// <summary>Detection timestamp (UTC) - when the incident first became real.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Application { get; set; } = "Unknown";
    public string Service { get; set; } = "Unknown";
    public string Environment { get; set; } = "Development";

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;
    public IncidentStatus Status { get; set; } = IncidentStatus.Detected;

    public string AffectedComponent { get; set; } = string.Empty;
    public string AffectedEndpoint { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Serialized list of human-readable symptom strings.</summary>
    public string SymptomsJson { get; set; } = "[]";

    /// <summary>Serialized list of Guid ids into the Incidents (telemetry) table.</summary>
    public string TelemetryReferencesJson { get; set; } = "[]";

    /// <summary>Serialized snapshot of the correlated metric signals that formed this incident.</summary>
    public string CorrelatedMetricsJson { get; set; } = "[]";

    // --- AI-derived fields. Advisory only; never presented as guaranteed fact. ---
    public string? RootCause { get; set; }
    public string? Summary { get; set; }
    public string? ContributingFactorsJson { get; set; }
    public double? Confidence { get; set; }
    public string? PredictedImpact { get; set; }
    public string? PredictedRisk { get; set; }
    public string? RecommendationsJson { get; set; }

    public RemediationStatus RemediationState { get; set; } = RemediationStatus.None;
    public VerificationStatus VerificationState { get; set; } = VerificationStatus.NotStarted;

    /// <summary>Set when the incident enters Failed/Rejected/Cancelled, or when AI is unavailable.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// True when signals have correlated in since the current diagnosis was produced.
    ///
    /// An incident keeps absorbing evidence while it is open, so a diagnosis reached from the first
    /// two signals can end up sitting above eighteen. Rather than silently rewriting a conclusion
    /// an operator may already be reading, the incident is flagged and re-investigation is offered
    /// as an explicit action.
    /// </summary>
    public bool DiagnosisStale { get; set; }

    /// <summary>Correlation grouping key - signals sharing this key fold into one incident (PRD section 8).</summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>Number of detection signals folded into this incident.</summary>
    public int SignalCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public List<IncidentEvent> Events { get; set; } = new();
    public List<IncidentEvidence> Evidence { get; set; } = new();
    public List<RemediationAction> Actions { get; set; } = new();
    public List<VerificationResult> Verifications { get; set; } = new();

    public bool IsTerminal =>
        Status is IncidentStatus.Resolved
               or IncidentStatus.Failed
               or IncidentStatus.Rejected
               or IncidentStatus.Cancelled;

    public bool IsOpen => !IsTerminal;
}
