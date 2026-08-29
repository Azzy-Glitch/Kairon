namespace Kairon.Backend.Models.Platform;

/// <summary>
/// The anchor every ProjectId Guid already scattered across Incident/Metric/AgentEvent/etc.
/// implicitly refers to today. Adapted from Azzy's productization branch (KaironProject there).
/// Slug/IsActive were added alongside the normalized telemetry pipeline
/// (docs/DESKTOP_SHELL.md) - everything that already keys off ProjectId is unaffected, since
/// these are new properties on the same entity/table, not a new identity.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>URL/log-friendly identifier, auto-derived from Name if not set explicitly.
    /// Not unique-enforced at the database level (a hackathon-scale single-tenant deployment has
    /// no real collision risk); revisit if this becomes a multi-tenant, internet-facing product.</summary>
    public string Slug { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
