using AIDIP.Backend.Configuration;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using AIDIP.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class PersistenceHardeningTests
{
    [Fact]
    public async Task RetentionDeletesExpiredRawDataButPreservesRecentAndOpenIncidentEvidence()
    {
        using var harness = new TestHarness();
        var now = DateTime.UtcNow;
        harness.Db.TelemetryReceipts.AddRange(Receipt(now.AddDays(-20)), Receipt(now.AddDays(-1)));
        harness.Db.Incidents.Add(new Incident { Id = Guid.NewGuid(), ProjectId = harness.ProjectId,
            Timestamp = now.AddDays(-40), Endpoint = "/expired", Environment = "Test" });
        var open = harness.SeedIncident(IncidentStatus.Investigating);
        open.UpdatedAt = now.AddDays(-300);
        harness.Db.Incidents.Add(new Incident { Id = Guid.NewGuid(), ProjectId = harness.ProjectId,
            Timestamp = now.AddDays(-300), Endpoint = "/evidence", Environment = "Test", SreIncidentId = open.Id });
        await harness.Db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(harness.Db).BuildServiceProvider();
        var options = Options.Create(new PersistenceOptions { RawTelemetryRetentionDays = 14,
            LegacySignalRetentionDays = 30, IncidentRetentionDays = 180, AuditRetentionDays = 365 });
        var state = new PersistenceMaintenanceState();
        var service = new PersistenceMaintenanceService(services.GetRequiredService<IServiceScopeFactory>(), options,
            state, NullLogger<PersistenceMaintenanceService>.Instance);

        await service.RunOnceAsync(default);
        harness.Db.ChangeTracker.Clear();

        Assert.Single(harness.Db.TelemetryReceipts);
        Assert.DoesNotContain(harness.Db.Incidents, x => x.Endpoint == "/expired");
        Assert.Contains(harness.Db.Incidents, x => x.Endpoint == "/evidence");
        Assert.Contains(harness.Db.SreIncidents, x => x.Id == open.Id);
        Assert.Equal("Healthy", state.Status);
    }

    [Fact]
    public async Task OnlineSqliteBackupIsConsistentAndReadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kairon-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "kairon.db");
            await using (var db = Create(database))
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new KaironProject { Id = Guid.NewGuid(), Name = "Preserved", Slug = "preserved",
                    CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            var service = new SqliteBackupService(Options.Create(new PersistenceOptions { Provider = "SQLite",
                DatabasePath = database, BackupRetentionCount = 2 }), NullLogger<SqliteBackupService>.Instance);
            var result = await service.CreateAsync("test");
            Assert.True(result.Created);
            var backup = Path.Combine(directory, "backups", result.FileName!);
            await using (var restored = Create(backup))
                Assert.Equal("Preserved", (await restored.Projects.SingleAsync()).Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SqliteHealthCheckValidatesIntegrity()
    {
        using var harness = new TestHarness();
        var check = new DatabaseHealthCheck(harness.Db, NullLogger<DatabaseHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("integrity", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static TelemetryReceipt Receipt(DateTime received) => new() { Id = Guid.NewGuid(), EventId = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(), EventTimestamp = received, EventType = "log", Severity = "Info", Source = "test",
        Application = "test", Service = "test", Environment = "Test", PayloadJson = "{}", ReceivedAt = received };
    private static AppDbContext Create(string path) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False")
        .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
}
