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
    /// <summary>Agent-confirmed evidence, never inferred from the selected target machine.</summary>
    public string MachineBindingStatus { get; set; } = "PendingAgentConfirmation";
    public DateTime? MachineBindingLastConfirmedAt { get; set; }
    public string ExpectedHostName { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public List<string> AllowedOperations { get; set; } = new();
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Execution readiness right now (TargetReadiness name: Ready, Disabled,
    /// PermissionMissing, ServiceMissing, MachineOffline, AwaitingAgentConfirmation, StaleTarget,
    /// ServiceIdentityChanged, Denylisted, RemoteNotSupported, UnsupportedPlatform, ...). Computed
    /// from the database and a read-only probe of the local SCM; never stored, never trusted as
    /// authorization - execution re-derives it independently.</summary>
    public string Readiness { get; set; } = "Disabled";
    public string? ReadinessDetail { get; set; }
    public string? ServiceDisplayName { get; set; }
    public bool ServiceIdentityConfirmed { get; set; }
}

/// <summary>One line of a remediation pre-flight. Blocking checks prevent enabling a target;
/// non-blocking ones (heartbeat, Agent confirmation of app telemetry) are expected to become true
/// once the application is running and only prevent execution.</summary>
public sealed class RemediationPreflightCheck
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    public bool Blocking { get; set; }
    public string? Detail { get; set; }
    /// <summary>The TargetReadiness this check maps to when it fails.</summary>
    public string? Readiness { get; set; }
}

public sealed class WindowsServiceDetails
{
    public string ServiceName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? ImagePath { get; set; }
    public string? StartAccount { get; set; }
    public bool CanQuery { get; set; }
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public string Eligibility { get; set; } = "Unknown";
    public string? EligibilityDetail { get; set; }
    public int? Win32Error { get; set; }
}

public sealed class RemediationPreflightResponse
{
    /// <summary>"Ready" when every check passes; otherwise the first failing check's readiness.</summary>
    public string Readiness { get; set; } = "";
    /// <summary>True when no blocking check failed - the target may be saved enabled.</summary>
    public bool CanEnable { get; set; }
    public List<RemediationPreflightCheck> Checks { get; set; } = new();
    public WindowsServiceDetails? Service { get; set; }
    /// <summary>Windows rights the selected operations require (Query/Start/Stop).</summary>
    public List<string> RequiredRights { get; set; } = new();
    public List<string> MissingRights { get; set; } = new();
    /// <summary>The Windows account the KAIRON backend executes SCM operations as.</summary>
    public string? ExecutorAccount { get; set; }
    public string? ExecutorSid { get; set; }
    /// <summary>Exact elevated command an administrator can run to grant only the missing rights
    /// on only this service to only the executor identity (tools/remediation). Never run by KAIRON.</summary>
    public string? FixCommand { get; set; }
}

public sealed class WindowsServiceListItem
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";
    public bool Eligible { get; set; }
    public string Eligibility { get; set; } = "";
    public string? EligibilityDetail { get; set; }
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
