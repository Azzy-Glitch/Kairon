using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using AIDIP.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class LocalPersistenceTests
{
    [Fact]
    public async Task FreshSqliteDatabaseMigratesAndPreservesTelemetryIncidentAndMachineAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kairon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kairon.db");
        var projectId = Guid.NewGuid();
        var telemetryId = Guid.NewGuid();
        var sreId = Guid.NewGuid();
        var machineId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        try
        {
            await using (var first = Create(path))
            {
                await first.Database.MigrateAsync();
                first.Incidents.Add(new Incident { Id = telemetryId, ProjectId = projectId, Timestamp = DateTime.UtcNow,
                    Endpoint = "/local", StatusCode = 500, Environment = "Local" });
                first.SreIncidents.Add(new SreIncident { Id = sreId, IncidentKey = "local-persist", ProjectId = projectId,
                    Timestamp = DateTime.UtcNow, Application = "test", Service = "test", Environment = "Local",
                    Title = "Persistence test", CorrelationKey = "local|test", CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow });
                first.Machines.Add(new Machine { Id = machineId, HostName = "test-machine", AgentCredentialHash = "hash",
                    RegisteredAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
                first.PlatformAuditEvents.Add(new PlatformAuditEvent { Id = auditId, Action = "project.created",
                    Actor = "local-operator", TargetType = "project", TargetId = projectId.ToString(),
                    ProjectId = projectId, Timestamp = DateTime.UtcNow });
                await first.SaveChangesAsync();
            }

            await using (var restarted = Create(path))
            {
                await restarted.Database.MigrateAsync();
                Assert.True(File.Exists(path));
                Assert.Equal(telemetryId, (await restarted.Incidents.SingleAsync()).Id);
                Assert.Equal(sreId, (await restarted.SreIncidents.SingleAsync()).Id);
                Assert.Equal(machineId, (await restarted.Machines.SingleAsync()).Id);
                Assert.Equal(auditId, (await restarted.PlatformAuditEvents.SingleAsync()).Id);
                Assert.Equal(6, (await restarted.Database.GetAppliedMigrationsAsync()).Count());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AppDbContext Create(string path) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False")
        .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
}
