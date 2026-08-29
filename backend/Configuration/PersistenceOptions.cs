namespace Kairon.Backend.Configuration;

/// <summary>
/// Selects and tunes the local persistence layer (docs/DESKTOP_SHELL.md). Ported from Azzy's
/// productization branch (origin/main, "codex/productization-windows-20260828") and adapted to
/// this codebase's model - SQLite is the real default for the packaged desktop product; SQL
/// Server remains fully supported for centralized/cloud deployments via an explicit override.
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>"SQLite" (default local mode, data under %LOCALAPPDATA%\Kairon) or "SqlServer"
    /// (centralized/cloud deployments, via ConnectionStrings:DefaultConnection).</summary>
    public string Provider { get; set; } = "SQLite";

    /// <summary>Explicit SQLite file path override. Empty means "use the resolved local-app-data
    /// layout" (backend/Infrastructure/KaironDataPaths.cs).</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>How long a normalized-telemetry idempotency receipt (TelemetryReceipt) is kept
    /// before PersistenceMaintenanceService prunes it - the raw payload, not the compatibility
    /// Incident/Metric rows it also produces, which follow LegacySignalRetentionDays instead.</summary>
    public int RawTelemetryRetentionDays { get; set; } = 14;

    public int LegacySignalRetentionDays { get; set; } = 30;
    public int IncidentRetentionDays { get; set; } = 180;
    public int AuditRetentionDays { get; set; } = 365;
    public int MaintenanceIntervalHours { get; set; } = 6;
    public int BackupRetentionCount { get; set; } = 5;
}
