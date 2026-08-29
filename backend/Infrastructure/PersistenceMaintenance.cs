using Kairon.Backend.Configuration;
using Kairon.Backend.Models.Sre;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// SQLite backup + persistence retention (docs/DESKTOP_SHELL.md), adapted from Azzy's
/// productization branch, kept near-verbatim onto this codebase's existing tables.
/// </summary>
public sealed class PersistenceMaintenanceState
{
    public DateTime? LastStartedAt { get; internal set; }
    public DateTime? LastCompletedAt { get; internal set; }
    public string Status { get; internal set; } = "NotRun";
    public int LastDeletedRows { get; internal set; }
    public string? LastError { get; internal set; }
}

public sealed record BackupResult(bool Created, string? FileName, string? Error);

public interface ISqliteBackupService
{
    Task<BackupResult> CreateAsync(string reason, CancellationToken cancellationToken = default);
}

public sealed class SqliteBackupService : ISqliteBackupService
{
    private readonly PersistenceOptions _options;
    private readonly ILogger<SqliteBackupService> _logger;

    public SqliteBackupService(IOptions<PersistenceOptions> options, ILogger<SqliteBackupService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<BackupResult> CreateAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!_options.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new BackupResult(false, null, "Backups are managed by the external database provider."));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = KaironDataPaths.Resolve(_options);
            paths.EnsureCreated();
            var sourcePath = string.IsNullOrWhiteSpace(_options.DatabasePath)
                ? paths.DatabasePath
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.DatabasePath));
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
                return Task.FromResult(new BackupResult(false, null, null));

            Directory.CreateDirectory(paths.Backups);
            var safeReason = new string(reason.Where(char.IsLetterOrDigit).Take(20).ToArray()).ToLowerInvariant();
            var fileName = $"kairon-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeReason}.db";
            var destinationPath = Path.Combine(paths.Backups, fileName);

            using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
            using var destination = new SqliteConnection($"Data Source={destinationPath};Mode=ReadWriteCreate;Pooling=False");
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);

            Prune(paths.Backups, Math.Clamp(_options.BackupRetentionCount, 1, 50));
            _logger.LogInformation("Created consistent SQLite backup {BackupFile} reason={Reason}", fileName, safeReason);
            return Task.FromResult(new BackupResult(true, fileName, null));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SQLite backup failed reason={Reason}", reason);
            return Task.FromResult(new BackupResult(false, null, "Local storage backup failed."));
        }
    }

    private static void Prune(string directory, int keep)
    {
        foreach (var file in new DirectoryInfo(directory).GetFiles("kairon-*.db")
                     .OrderByDescending(x => x.CreationTimeUtc).Skip(keep))
            file.Delete();
    }
}

public sealed class PersistenceMaintenanceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PersistenceOptions _options;
    private readonly PersistenceMaintenanceState _state;
    private readonly ILogger<PersistenceMaintenanceService> _logger;

    public PersistenceMaintenanceService(IServiceScopeFactory scopes, IOptions<PersistenceOptions> options,
        PersistenceMaintenanceState state, ILogger<PersistenceMaintenanceService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Clamp(_options.MaintenanceIntervalHours, 1, 168)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        _state.LastStartedAt = DateTime.UtcNow;
        _state.Status = "Running";
        _state.LastError = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var deleted = 0;

            deleted += await db.TelemetryReceipts
                .Where(x => x.ReceivedAt < now.AddDays(-Days(_options.RawTelemetryRetentionDays, 1)))
                .ExecuteDeleteAsync(cancellationToken);
            deleted += await db.Metrics
                .Where(x => x.Timestamp < now.AddDays(-Days(_options.LegacySignalRetentionDays, 1)))
                .ExecuteDeleteAsync(cancellationToken);
            deleted += await db.Incidents
                .Where(x => x.SreIncidentId == null && x.Timestamp < now.AddDays(-Days(_options.LegacySignalRetentionDays, 1)))
                .ExecuteDeleteAsync(cancellationToken);

            var terminal = new[] { IncidentStatus.Resolved, IncidentStatus.Failed, IncidentStatus.Rejected, IncidentStatus.Cancelled };
            var incidentCutoff = now.AddDays(-Days(_options.IncidentRetentionDays, 90));
            var expiringIncidentIds = await db.SreIncidents
                .Where(x => terminal.Contains(x.Status) && x.UpdatedAt < incidentCutoff)
                .Select(x => x.Id).ToListAsync(cancellationToken);
            deleted += await db.Incidents
                .Where(x => x.SreIncidentId.HasValue && expiringIncidentIds.Contains(x.SreIncidentId.Value)
                    && x.Timestamp < incidentCutoff)
                .ExecuteDeleteAsync(cancellationToken);
            // Never delete a non-terminal incident, however old - an open/in-progress incident is
            // never eligible for retention cleanup regardless of age.
            deleted += await db.SreIncidents
                .Where(x => terminal.Contains(x.Status) && x.UpdatedAt < incidentCutoff)
                .ExecuteDeleteAsync(cancellationToken);

            deleted += await db.PlatformAuditEvents
                .Where(x => x.Timestamp < now.AddDays(-Days(_options.AuditRetentionDays, 30)))
                .ExecuteDeleteAsync(cancellationToken);

            _state.LastDeletedRows = deleted;
            _state.LastCompletedAt = DateTime.UtcNow;
            _state.Status = "Healthy";
            _logger.LogInformation("Persistence maintenance completed deleted={DeletedRows}", deleted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _state.Status = "Degraded";
            _state.LastError = "Persistence maintenance failed.";
            _logger.LogError(exception, "Persistence maintenance failed");
        }
    }

    private static int Days(int configured, int minimum) => Math.Clamp(configured, minimum, 3650);
}
