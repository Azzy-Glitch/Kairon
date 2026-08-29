namespace Kairon.Backend.Configuration;

/// <summary>
/// Selects and tunes the local persistence layer (docs/DESKTOP_SHELL.md). Ported from Azzy's
/// productization branch (origin/main, "codex/productization-windows-20260828") and adapted to
/// this codebase's model - the retention fields here only reference tables that actually exist
/// on this branch (no TelemetryReceipts, which belongs to the deferred multi-tenant platform
/// model, not built here).
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>"SQLite" (default local mode, data under %LOCALAPPDATA%\Kairon) or "SqlServer"
    /// (existing centralized/dev behavior, via ConnectionStrings:DefaultConnection).</summary>
    public string Provider { get; set; } = "SqlServer";

    /// <summary>Explicit SQLite file path override. Empty means "use the resolved local-app-data
    /// layout" (backend/Infrastructure/KaironDataPaths.cs).</summary>
    public string DatabasePath { get; set; } = string.Empty;

    public int LegacySignalRetentionDays { get; set; } = 30;
    public int IncidentRetentionDays { get; set; } = 180;
    public int AuditRetentionDays { get; set; } = 365;
    public int MaintenanceIntervalHours { get; set; } = 6;
    public int BackupRetentionCount { get; set; } = 5;
}
