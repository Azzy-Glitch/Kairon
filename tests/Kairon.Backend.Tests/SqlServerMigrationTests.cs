using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class SqlServerMigrationTests
{
    [Fact]
    public void ExistingForwardMigrationGeneratesBothNullableMachineColumns()
    {
        using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var sql=db.GetService<IMigrator>().GenerateScript("20260904163834_AddAiProviderConfig");
        Assert.Contains("ALTER TABLE [Incidents] ADD [MachineId] uniqueidentifier NULL",sql);
        Assert.Contains("ALTER TABLE [Metrics] ADD [MachineId] uniqueidentifier NULL",sql);
    }

    [SqlServerFact]
    public Task FreshSqlServerDatabaseSupportsMachineScopedTelemetry() => ValidateAsync(false);
    [SqlServerFact]
    public Task ExistingSqlServerTelemetrySurvivesForwardUpgrade() => ValidateAsync(true);

    private static async Task ValidateAsync(bool upgrade)
    {
        var builder=new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("KAIRON_TEST_SQLSERVER") ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true");
        // Never migrate or delete the supplied database: each test owns a new random database.
        builder.InitialCatalog="Kairon_MigrationTest_"+Guid.NewGuid().ToString("N");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(builder.ConnectionString).Options);
        try
        {
            if(upgrade)
            {
                await db.GetService<IMigrator>().MigrateAsync("20260904163834_AddAiProviderConfig");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Metrics (Id,ProjectId,Timestamp,RequestCount,ErrorCount,Environment) VALUES (NEWID(),NEWID(),SYSUTCDATETIME(),1,0,'Production')");
            }
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            if(upgrade)Assert.Null((await db.Metrics.SingleAsync()).MachineId);
            var machine=Guid.NewGuid();var project=Guid.NewGuid();
            db.Metrics.Add(new Metric {Id=Guid.NewGuid(),ProjectId=project,MachineId=machine,Timestamp=DateTime.UtcNow,Environment="Production"});
            db.Incidents.Add(new Incident {Id=Guid.NewGuid(),ProjectId=project,MachineId=machine,Timestamp=DateTime.UtcNow,Environment="Production"});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            Assert.Single(await db.Metrics.Where(m=>m.MachineId==machine).ToListAsync());
            Assert.Single(await db.Incidents.Where(i=>i.MachineId==machine).ToListAsync());
            await db.Database.MigrateAsync(); // Reapplying the chain must be harmless.
        }
        finally {await db.Database.EnsureDeletedAsync();}
    }
}
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if(!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KAIRON_TEST_SQLSERVER")))
            Skip="Requires Windows SQL Server LocalDB or an explicit KAIRON_TEST_SQLSERVER test server.";
    }
}
