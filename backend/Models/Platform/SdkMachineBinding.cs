namespace Kairon.Backend.Models.Platform;

/// <summary>
/// Last machine independently confirmed by an enrolled Agent for this exact project credential.
/// This is evidence for the operator UI, not a reusable authorization token: every machine-scoped
/// telemetry request must arrive through the Agent relay and present fresh Agent authentication.
/// A credential can bind to only one machine; re-pairing issues a different credential Id.
/// </summary>
public sealed class SdkMachineBinding
{
    public Guid CredentialId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid MachineId { get; set; }
    public string AgentCredentialHash { get; set; } = string.Empty;
    public DateTime LastConfirmedAt { get; set; }

    /// <summary>The process that sent the last Agent-confirmed telemetry, as observed by the
    /// Agent from the operating system (the owner of the loopback connection that carried the
    /// proof) - never a value the application reported about itself.</summary>
    public int? ProcessId { get; set; }

    /// <summary>The working directory and executable the SDK in that same process reported. Only
    /// recorded when the SDK's own process id equals the Agent-observed one; used to relaunch the
    /// application in the right folder, and shown to the operator.</summary>
    public string? ProcessWorkingDirectory { get; set; }
    public string? ProcessExecutable { get; set; }
}

/// <summary>
/// Single-use challenge tied to one SDK credential, one service/environment and the SHA-256 of
/// one HTTP telemetry body. Only the enrolled Agent may confirm its machine. The telemetry
/// endpoint atomically consumes it; a prior confirmation cannot authorize another payload.
/// </summary>
public sealed class SdkMachineProofChallenge
{
    public Guid Id { get; set; }
    public Guid CredentialId { get; set; }
    public Guid ProjectId { get; set; }
    public string EnvironmentNormalized { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string BodySha256 { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public Guid? MachineId { get; set; }
    public string? AgentCredentialHash { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Process that owned the loopback connection the Agent received this proof on, as
    /// reported by the Agent from the OS TCP table. Null for Agents that predate this.</summary>
    public int? ProcessId { get; set; }

    public Guid RowVersion { get; set; } = Guid.NewGuid();
}
