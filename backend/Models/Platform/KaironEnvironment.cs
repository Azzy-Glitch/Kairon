namespace Kairon.Backend.Models.Platform;

/// <summary>
/// One deployment environment ("Production", "Staging", ...) under a Project, auto-created the
/// first time normalized telemetry names it (see PlatformTelemetryService). Named
/// KaironEnvironment (not "Environment") to avoid colliding with System.Environment throughout
/// this codebase. Adapted from Azzy's productization branch.
/// </summary>
public sealed class KaironEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
