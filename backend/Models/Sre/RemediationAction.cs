namespace AIDIP.Backend.Models.Sre;

/// <summary>
/// A typed, registry-bound remediation action (PRD section 13). The AI can only *propose* one of
/// these by naming a registered tool; the backend validates it against policy and requires human
/// approval before any execution.
/// </summary>
public class RemediationAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public SreIncident? Incident { get; set; }

    /// <summary>Human-facing short key, e.g. ACT-0001.</summary>
    public string ActionKey { get; set; } = string.Empty;

    /// <summary>Registered tool name. Anything not in the registry is rejected by policy.</summary>
    public string ActionType { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
    public string ExpectedOutcome { get; set; } = string.Empty;
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Medium;

    /// <summary>Always true initially (PRD section 13: approval required for every remediation).</summary>
    public bool RequiresApproval { get; set; } = true;

    public RemediationStatus Status { get; set; } = RemediationStatus.Proposed;

    /// <summary>Bounded JSON of tool parameters, validated by the tool before execution.</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>Origin of the proposal: "ai" or "policy-fallback".</summary>
    public string Source { get; set; } = "ai";

    public string? PolicyDecision { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? ExecutionResult { get; set; }
    public string? ExecutionError { get; set; }

    public Guid? VerificationResultId { get; set; }
}
