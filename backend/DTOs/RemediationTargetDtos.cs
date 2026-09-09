namespace Kairon.Backend.DTOs;

// Operator-facing DTOs for remediation-target management (Phase 2, built on the Phase 1
// database-backed RemediationTarget entity - see Models/Platform/RemediationTarget.cs and
// Services/Remediation/RemediationTargetManagementService.cs). The entity is never returned or
// accepted directly: Machine.AgentCredentialHash and ProjectApiCredential.KeyHash, reachable
// through the bound machine/credential, must never reach an HTTP response, and AllowedOperations
// is exposed as a validated list rather than the raw AllowedOperationsJson column.

public sealed class RemediationTargetResponse
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Denormalized for the list view (frontend PRD: Remediation Targets table's Project
    /// column) - never authoritative, always re-read from Project at response time.</summary>
    public string? ProjectName { get; set; }

    public string Environment { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public Guid MachineId { get; set; }

    /// <summary>The machine's current registered host name - distinct from ExpectedHostName below,
    /// which is the operator-declared binding the runtime resolver compares it against.</summary>
    public string? MachineHostName { get; set; }

    public Guid TelemetryCredentialId { get; set; }
    public string? TelemetryCredentialName { get; set; }
    public string ExpectedHostName { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public List<string> AllowedOperations { get; set; } = new();
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateRemediationTargetRequest
{
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public Guid MachineId { get; set; }
    public Guid TelemetryCredentialId { get; set; }
    public string ExpectedHostName { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public List<string> AllowedOperations { get; set; } = new();

    /// <summary>Defaults to true. A caller may stage a target disabled; a disabled target is exempt
    /// from project/machine/credential/uniqueness validation (RemediationTarget.Enabled's own
    /// documented invariant: a disabled row may harmlessly go stale) and is fully revalidated again
    /// the moment it is enabled - see RemediationTargetManagementService.SetEnabledAsync.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class UpdateRemediationTargetRequest : CreateRemediationTargetRequest
{
    /// <summary>Optional optimistic-concurrency guard. When supplied, must match the target's
    /// current UpdatedAt or the update is refused with a conflict instead of silently overwriting a
    /// concurrent change. This codebase has no rowversion/ETag convention to reuse, so the existing
    /// UpdatedAt column doubles as the smallest viable concurrency token rather than introducing a
    /// new one.</summary>
    public DateTime? ExpectedUpdatedAt { get; set; }
}

public sealed class RemediationTargetValidationResponse
{
    public bool Valid { get; set; }
    public List<string> Errors { get; set; } = new();
}
