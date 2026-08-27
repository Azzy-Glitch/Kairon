namespace AIDIP.Backend.Configuration;

public sealed class PlatformSecurityOptions
{
    public const string SectionName = "PlatformSecurity";
    public bool RequireTelemetryKey { get; set; }
    public string TelemetryKeyHeader { get; set; } = "X-KAIRON-API-Key";
}
