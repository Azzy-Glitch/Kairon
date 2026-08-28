namespace AIDIP.Backend.Models;

public sealed class KaironProject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public ICollection<MonitoredApplication> Applications { get; set; } = new List<MonitoredApplication>();
    public ICollection<KaironEnvironment> Environments { get; set; } = new List<KaironEnvironment>();
    public ICollection<ProjectApiCredential> Credentials { get; set; } = new List<ProjectApiCredential>();
}

public sealed class MonitoredApplication
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public KaironProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Runtime { get; set; } = "Unknown";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTelemetryAt { get; set; }
    public ICollection<TelemetrySourceRegistration> TelemetrySources { get; set; } = new List<TelemetrySourceRegistration>();
}

public sealed class KaironEnvironment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public KaironProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class TelemetrySourceRegistration
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? ApplicationId { get; set; }
    public MonitoredApplication? Application { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class TelemetryReceipt
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? SourceId { get; set; }
    public DateTime EventTimestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

public sealed class ProjectApiCredential
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public KaironProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class SdkPairingSession
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ApplicationId { get; set; }
    public string SdkType { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class SdkInstallation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid SourceId { get; set; }
    public string SdkType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallationId { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
