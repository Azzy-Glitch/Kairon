namespace AIDIP.Backend.Configuration;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";
    public string Provider { get; set; } = "SQLite";
    public string DatabasePath { get; set; } = string.Empty;
    public int RawTelemetryRetentionDays { get; set; } = 14;
    public int LegacySignalRetentionDays { get; set; } = 30;
    public int IncidentRetentionDays { get; set; } = 180;
    public int AuditRetentionDays { get; set; } = 365;
    public int MaintenanceIntervalHours { get; set; } = 6;
    public int BackupRetentionCount { get; set; } = 5;
}
