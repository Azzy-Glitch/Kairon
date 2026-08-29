namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A revocable credential scoped to one specific SDK installation (a "ksi_" key), stronger than
/// the existing project-level ProjectApiCredential ("krn_") because compromising one installation
/// does not expose every other installation on the same project. Optional, additive tier -
/// existing project-level pairing (SdkPairingSession/ProjectApiCredential) is unaffected and
/// remains the default flow for both SDKs' pair() calls. Adapted from Azzy's productization
/// branch.
/// </summary>
public sealed class SdkInstallation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid SourceId { get; set; }
    public string SdkType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
