using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Persistence retention (docs/DESKTOP_SHELL.md): bounded database growth, but a non-terminal
/// (still in-progress) incident is never eligible for cleanup regardless of age.
/// </summary>
public sealed class PersistenceRetentionTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly ServiceProvider _provider;

    public PersistenceRetentionTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_h.Db);
        _provider = services.BuildServiceProvider();
    }

    private PersistenceMaintenanceService Service(PersistenceOptions options) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(), TestHarness.Opt(options),
        new PersistenceMaintenanceState(), NullLogger<PersistenceMaintenanceService>.Instance);

    [Fact]
    public async Task ExpiredTerminalIncidentIsDeletedButOpenIncidentNeverIs()
    {
        var old = DateTime.UtcNow.AddDays(-200);
        var resolved = _h.SeedIncident(status: IncidentStatus.Resolved);
        resolved.UpdatedAt = old;
        var stillOpen = _h.SeedIncident(status: IncidentStatus.Investigating, correlationKey: "open-one");
        stillOpen.UpdatedAt = old;
        _h.Db.SaveChanges();

        await Service(new PersistenceOptions { IncidentRetentionDays = 90 }).RunOnceAsync(default);

        Assert.False(_h.Db.SreIncidents.Any(x => x.Id == resolved.Id));
        Assert.True(_h.Db.SreIncidents.Any(x => x.Id == stillOpen.Id));
    }

    [Fact]
    public async Task ExpiredTelemetryReceiptIsDeleted()
    {
        _h.Db.TelemetryReceipts.Add(new TelemetryReceipt
        {
            ProjectId = _h.ProjectId, EventId = Guid.NewGuid(), EventType = "exception", Source = "dotnet-sdk",
            Application = "Kairon.DemoApp", Service = _h.Service, Environment = _h.Environment,
            ReceivedAt = DateTime.UtcNow.AddDays(-30)
        });
        _h.Db.SaveChanges();

        await Service(new PersistenceOptions { RawTelemetryRetentionDays = 14 }).RunOnceAsync(default);

        Assert.Empty(_h.Db.TelemetryReceipts);
    }

    [Fact]
    public async Task FreshDataIsNotDeleted()
    {
        var incident = _h.SeedIncident(status: IncidentStatus.Resolved);
        incident.UpdatedAt = DateTime.UtcNow;
        _h.Db.SaveChanges();

        await Service(new PersistenceOptions { IncidentRetentionDays = 90 }).RunOnceAsync(default);

        Assert.True(_h.Db.SreIncidents.Any(x => x.Id == incident.Id));
    }

    public void Dispose()
    {
        _h.Dispose();
        _provider.Dispose();
    }
}

/// <summary>SQLite online backup - real file I/O, so this uses a genuine on-disk database rather
/// than TestHarness's in-memory connection.</summary>
public sealed class SqliteBackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kairon-backup-tests-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "kairon.db");

    public SqliteBackupServiceTests()
    {
        Directory.CreateDirectory(_directory);
        // Pooling=False: a pooled connection keeps the underlying file handle open even after
        // Dispose(), which would make Dispose() below fail to delete the directory on Windows.
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Probe (Id INTEGER PRIMARY KEY); INSERT INTO Probe VALUES (1);";
        command.ExecuteNonQuery();
    }

    private SqliteBackupService Service(int retentionCount = 5) => new(
        TestHarness.Opt(new PersistenceOptions { Provider = "SQLite", DatabasePath = DatabasePath, BackupRetentionCount = retentionCount }),
        NullLogger<SqliteBackupService>.Instance);

    [Fact]
    public async Task CreatesABackupFileOfTheRealDatabase()
    {
        var result = await Service().CreateAsync("test");

        Assert.True(result.Created);
        Assert.NotNull(result.FileName);
        Assert.True(File.Exists(Path.Combine(_directory, "backups", result.FileName!)));
    }

    [Fact]
    public async Task PrunesOldBackupsBeyondRetentionCount()
    {
        var service = Service(retentionCount: 2);
        for (var i = 0; i < 4; i++)
        {
            // Distinct reason suffixes keep filenames unique even at second-resolution timestamps,
            // so no real delay is needed between backups here.
            await service.CreateAsync("reason" + i);
        }

        var remaining = Directory.GetFiles(Path.Combine(_directory, "backups"), "kairon-*.db");
        Assert.Equal(2, remaining.Length);
    }

    [Fact]
    public async Task SqlServerProviderIsANoOpNotAnError()
    {
        var service = new SqliteBackupService(
            TestHarness.Opt(new PersistenceOptions { Provider = "SqlServer" }), NullLogger<SqliteBackupService>.Instance);

        var result = await service.CreateAsync("test");

        Assert.False(result.Created);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
