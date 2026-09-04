using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class DataManagementServiceTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kairon-data-management-tests-" + Guid.NewGuid().ToString("N"));

    private PersistenceOptions Options => new()
    {
        Provider = "SQLite",
        DatabasePath = Path.Combine(_directory, "kairon.db")
    };

    public DataManagementServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DeleteAllRemovesDatabaseRecordsAndOnlyManagedBackups()
    {
        _h.Db.Projects.Add(new Project { Id = _h.ProjectId, Name = "Order API", Slug = "order-api" });
        _h.SeedMetric(DateTime.UtcNow, cpu: 12, requests: 4);
        _h.SeedTelemetry(DateTime.UtcNow, statusCode: 200, errorType: null, errorMessage: null);
        _h.Db.SaveChanges();

        var backupDirectory = Path.Combine(_directory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var managedBackup = Path.Combine(backupDirectory, "kairon-20260904-userexport.db");
        var unrelated = Path.Combine(backupDirectory, "keep-me.txt");
        await File.WriteAllTextAsync(managedBackup, "database");
        await File.WriteAllTextAsync(unrelated, "unrelated");

        var result = await Service().DeleteAllAsync();

        Assert.Empty(_h.Db.Projects);
        Assert.Empty(_h.Db.Metrics);
        Assert.Empty(_h.Db.Incidents);
        Assert.True(result.DeletedRecords >= 3);
        Assert.Equal(1, result.DeletedBackups);
        Assert.False(File.Exists(managedBackup));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task ExportResolvesOnlyTheBackupCreatedByTheBackupService()
    {
        var backupDirectory = Path.Combine(_directory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupName = "kairon-export.db";
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, backupName), "database");
        var service = new DataManagementService(
            _h.Db,
            new StubBackupService(new BackupResult(true, backupName, null)),
            TestHarness.Opt(Options),
            NullLogger<DataManagementService>.Instance);

        var result = await service.CreateExportAsync();

        Assert.True(result.Created);
        Assert.Equal(Path.Combine(backupDirectory, backupName), result.FullPath);
        Assert.StartsWith("kairon-data-", result.FileName);
    }

    private DataManagementService Service() => new(
        _h.Db,
        new StubBackupService(new BackupResult(false, null, null)),
        TestHarness.Opt(Options),
        NullLogger<DataManagementService>.Instance);

    public void Dispose()
    {
        _h.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class StubBackupService(BackupResult result) : ISqliteBackupService
    {
        public Task<BackupResult> CreateAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
