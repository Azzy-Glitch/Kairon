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
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}
