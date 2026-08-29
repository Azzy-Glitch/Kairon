namespace Kairon.Backend.Models.Platform;

/// <summary>
/// The anchor every ProjectId Guid already scattered across Incident/Metric/AgentEvent/etc.
/// implicitly refers to today. Adapted from Azzy's productization branch (KaironProject there),
/// deliberately without its Applications/Environments hierarchy - this codebase scopes telemetry
/// by ProjectId+Environment+Service already (free-text fields on the existing models), so a
/// separate modeled Application/Environment tree is not needed for pairing to mean something.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
