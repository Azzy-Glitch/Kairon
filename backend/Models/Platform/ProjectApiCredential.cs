namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A long-lived, revocable per-project credential. Only KeyPrefix (first 12 chars, for lookup)
/// and a SHA-256 hash of the full key are ever stored - the raw key is returned once, at
/// creation/redemption, and never again. Ported from Azzy's productization branch.
/// </summary>
public sealed class ProjectApiCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}
