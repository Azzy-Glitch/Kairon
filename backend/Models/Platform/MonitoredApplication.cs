namespace Kairon.Backend.Models.Platform;

/// <summary>
/// One application/service under a Project, auto-created the first time normalized telemetry
/// arrives for it (see PlatformTelemetryService) - not something an operator provisions by hand.
/// Adapted from Azzy's productization branch's KaironProject/MonitoredApplication hierarchy,
/// added additively on top of the existing Project entity rather than replacing it.
/// </summary>
public sealed class MonitoredApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Runtime { get; set; } = "Unknown";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTelemetryAt { get; set; }
}
