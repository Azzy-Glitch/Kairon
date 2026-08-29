namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A short-lived (10 minute) pairing code, hashed at rest, redeemed exactly once for a real
/// ProjectApiCredential. Adapted from Azzy's productization branch: that version anchored a
/// session to a MonitoredApplication (a modeled entity this codebase doesn't have); here it
/// anchors directly to Project, matching how every other telemetry path already scopes itself.
/// </summary>
public sealed class SdkPairingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SdkType { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
