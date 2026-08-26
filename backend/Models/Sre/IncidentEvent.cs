namespace Kairon.Backend.Models.Sre;

/// <summary>
/// Immutable audit-trail entry (PRD section 14). One row per meaningful thing that happened to
/// an incident: state changes, AI calls, policy decisions, approvals, executions, verifications.
/// </summary>
public class IncidentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public SreIncident? Incident { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Detected, Investigating, Diagnosed, Recommended, Approved, Rejected, Executed, Verified, Resolved, Failed.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Who or what caused it: "detection-engine", "ai-orchestrator", "operator:alice", "policy".</summary>
    public string Actor { get; set; } = "system";

    public string? PreviousState { get; set; }
    public string? NewState { get; set; }

    public string? ActionId { get; set; }
    public string? Result { get; set; }
    public string? Message { get; set; }

    /// <summary>Redacted error information where applicable - never contains secrets.</summary>
    public string? Error { get; set; }

    /// <summary>Optional structured payload (JSON) for operator inspection.</summary>
    public string? DataJson { get; set; }
}

/// <summary>Canonical audit event names, so the audit trail stays greppable.</summary>
public static class IncidentEventTypes
{
    public const string Detected = "Detected";
    public const string Correlated = "Correlated";
    public const string Investigating = "Investigating";
    public const string Diagnosed = "Diagnosed";
    public const string Predicted = "Predicted";
    public const string Recommended = "Recommended";
    public const string PolicyEvaluated = "PolicyEvaluated";
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Executing = "Executing";
    public const string Executed = "Executed";
    public const string Verifying = "Verifying";
    public const string Verified = "Verified";
    public const string Resolved = "Resolved";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}
