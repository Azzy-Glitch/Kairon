using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services;

public sealed record DataExportResult(bool Created, string? FullPath, string? FileName, string? Error);
public sealed record DataDeletionResult(int DeletedRecords, int DeletedBackups);

public interface IDataManagementService
{
    Task<DataExportResult> CreateExportAsync(CancellationToken cancellationToken = default);
    Task<DataDeletionResult> DeleteAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// User-invoked data portability and deletion. Export uses SQLite's online backup API, so the
/// downloaded database is consistent even while telemetry is arriving. Deletion removes records
/// but leaves migrations/schema intact so the running desktop application can continue safely.
/// </summary>
public sealed class DataManagementService : IDataManagementService
{
    private readonly AppDbContext _db;
    private readonly ISqliteBackupService _backups;
    private readonly PersistenceOptions _options;
    private readonly ILogger<DataManagementService> _logger;

    public DataManagementService(
        AppDbContext db,
        ISqliteBackupService backups,
        IOptions<PersistenceOptions> options,
        ILogger<DataManagementService> logger)
    {
        _db = db;
        _backups = backups;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DataExportResult> CreateExportAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.Database.IsSqlite())
            return new(false, null, null, "Database download is available for local SQLite storage only.");

        var backup = await _backups.CreateAsync("user-export", cancellationToken);
        if (!backup.Created || string.IsNullOrWhiteSpace(backup.FileName))
            return new(false, null, null, backup.Error ?? "There is no database to download yet.");

        var paths = KaironDataPaths.Resolve(_options);
        var backupRoot = Path.GetFullPath(paths.Backups).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(paths.Backups, backup.FileName));
        if (!fullPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return new(false, null, null, "The database export could not be opened safely.");

        return new(true, fullPath, $"kairon-data-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db", null);
    }

    public async Task<DataDeletionResult> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Dependants first. This order works with foreign keys enabled and remains explicit,
            // reviewable and provider-independent instead of dynamically deleting unknown tables.
            deleted += await _db.VerificationResults.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.RemediationActions.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.IncidentEvidence.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.IncidentEvents.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Incidents.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.SreIncidents.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Metrics.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Analyses.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.AgentEvents.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.TelemetryReceipts.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.SdkInstallations.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.TelemetrySources.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Environments.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.MonitoredApplications.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.ProjectApiCredentials.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.SdkPairingSessions.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.PlatformAuditEvents.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.DiscoveredApplications.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Machines.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.AiProviderConfigs.ExecuteDeleteAsync(cancellationToken);
            deleted += await _db.Projects.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var deletedBackups = DeleteManagedBackups();

        if (_db.Database.IsSqlite())
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
            await _db.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
        }

        _logger.LogWarning(
            "Operator deleted all Kairon database data records={DeletedRecords} backups={DeletedBackups}",
            deleted, deletedBackups);
        return new(deleted, deletedBackups);
    }

    private int DeleteManagedBackups()
    {
        if (!_options.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase)) return 0;

        var directory = KaironDataPaths.Resolve(_options).Backups;
        if (!Directory.Exists(directory)) return 0;

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "kairon-*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Could not delete managed Kairon backup {BackupFile}", file);
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogWarning(exception, "Could not delete managed Kairon backup {BackupFile}", file);
            }
        }
        return deleted;
    }
}
