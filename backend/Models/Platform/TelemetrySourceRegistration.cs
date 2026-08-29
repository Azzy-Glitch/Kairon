namespace Kairon.Backend.Models.Platform;

/// <summary>
/// One reporting source (an SDK installation, the Agent, a manual integration) for a
/// MonitoredApplication, identified by a caller-supplied or derived InstallationId and
/// auto-registered the first time it sends normalized telemetry. Adapted from Azzy's
/// productization branch. Distinct from SdkInstallation: this row is "some source is reporting
/// telemetry for this application" (created by ingestion itself, no credential); SdkInstallation
/// is "this specific SDK install has been issued a revocable credential" (created by pairing).
/// </summary>
public sealed class TelemetrySourceRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ApplicationId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
}
