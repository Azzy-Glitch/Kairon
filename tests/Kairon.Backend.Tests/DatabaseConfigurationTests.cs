using System.Text.Json;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;
public sealed class DatabaseConfigurationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "Kairon_DbSettings_" + Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider protection = new();
    private readonly Probe probe = new();
    private string FilePath => Path.Combine(folder, "database-settings.protected");
    private DatabaseConfigurationService Service(string environment = "Development") => new(new(FilePath), protection,
        new("SQLite", "SQLite"), probe, new Host { EnvironmentName = environment }, NullLogger<DatabaseConfigurationService>.Instance);
    private static DatabaseSettingsRequest Sql(string? password = "test-secret-never-return") => new() { Provider = "SqlServer", Server = "server", Database = "existing", Authentication = "SqlLogin", UserName = "operator", Password = password };
    [Fact] public async Task DefaultSQLiteNeedsNoServerOrSavedFile()
    {
        var service = Service(); Assert.Equal("SQLite", service.Get().ActiveProvider);
        Assert.True((await service.TestAsync(new(), default)).Success);
        Assert.False((await service.SaveAsync(new(), default)).RequiresRestart); Assert.Equal(0, probe.Calls);
    }
    [Fact] public async Task SaveEncryptsPasswordAndOnlyReturnsPresence()
    {
        var service = Service(); var result = await service.SaveAsync(Sql(), default);
        Assert.True(result.RequiresRestart); Assert.True(result.Selected.HasPassword); Assert.Equal("SQLite", result.ActiveProvider);
        Assert.DoesNotContain("test-secret", File.ReadAllText(FilePath));
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(service.Get()));
        Assert.Equal("test-secret-never-return", DatabaseConfigurationService.Read(FilePath, protection.CreateProtector(DatabaseConfigurationService.Purpose))!.Password);
    }
    [Fact] public async Task FailedConnectionPreservesPreviousSettings()
    {
        var service = Service(); await service.SaveAsync(new(), default); var before = File.ReadAllText(FilePath);
        probe.Success = false; await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(Sql(), default));
        Assert.Equal(before, File.ReadAllText(FilePath));
    }
    [Fact] public async Task BlankPasswordReusesOnlySameConnection()
    {
        var service = Service(); await service.SaveAsync(Sql(), default); await service.TestAsync(Sql(null), default);
        Assert.Contains("test-secret-never-return", probe.Last);
        var changed = Sql(null); changed.Server = "different";
        await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => service.TestAsync(changed, default)); Assert.Equal(2, probe.Calls);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, true)]
    public async Task ProductionRejectsInsecureTransport(bool encrypt, bool trust)
    {
        var request = Sql(); request.Encrypt = encrypt; request.TrustServerCertificate = trust;
        await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service("Production").SaveAsync(request, default)); Assert.Equal(0, probe.Calls);
    }
    [Fact] public async Task SQLiteSelectionDiscardsSqlCredentials()
    {
        var service = Service(); await service.SaveAsync(Sql(), default); var request = Sql(); request.Provider = "SQLite";
        var result = await service.SaveAsync(request, default); Assert.False(result.Selected.HasPassword); Assert.Equal("", result.Selected.Server);
        Assert.Null(DatabaseConfigurationService.Read(FilePath, protection.CreateProtector(DatabaseConfigurationService.Purpose))!.Password);
    }
    [Fact] public void CorruptConfigurationFailsClosedWithoutExposingContents()
    {
        Directory.CreateDirectory(folder); File.WriteAllText(FilePath, "private-corrupt-value");
        var error = Assert.Throws<InvalidOperationException>(() => Service().Get()); Assert.DoesNotContain("private-corrupt-value", error.ToString());
    }
    [Fact] public async Task TestDoesNotSaveOrChangeActiveProvider()
    {
        Assert.True((await Service().TestAsync(Sql(), default)).Success); Assert.False(File.Exists(FilePath)); Assert.Equal("SQLite", Service().Get().ActiveProvider);
    }
    [Fact] public async Task InvalidProviderNeverContactsServer()
    {
        await Assert.ThrowsAsync<DatabaseSettingsValidationException>(() => Service().SaveAsync(new() { Provider = "Hackathon" }, default)); Assert.Equal(0, probe.Calls);
    }
    [SqlServerFact] public async Task RealSqlServerProbeOpensExistingDatabaseWithoutProvisioning()
    {
        var connection = Environment.GetEnvironmentVariable("KAIRON_TEST_SQLSERVER") ?? @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true";
        Assert.True(await new SqlServerConnectionProbe().TestAsync(connection, default));
        var missing = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection) { InitialCatalog = "Kairon_Missing_" + Guid.NewGuid().ToString("N") };
        Assert.False(await new SqlServerConnectionProbe().TestAsync(missing.ConnectionString, default));
    }
    [Fact] public async Task BootstrapWithoutSavedSettingsPreservesDefaults()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        var paths = Kairon.Backend.Infrastructure.KaironDataPaths.Resolve(new Kairon.Backend.Configuration.PersistenceOptions { DatabasePath = Path.Combine(folder, "kairon.db") });
        await DatabaseConfigurationService.ApplySavedAtStartupAsync(config, paths, false);
        Assert.Null(config["Persistence:Provider"]); Assert.False(Directory.Exists(folder));
    }
    [Fact] public async Task SavedSQLiteAppliesOnRestartUsingExistingKeyRing()
    {
        var paths = Kairon.Backend.Infrastructure.KaironDataPaths.Resolve(new Kairon.Backend.Configuration.PersistenceOptions { DatabasePath = Path.Combine(folder, "kairon.db") });
        var diskProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(paths.Config, "dataprotection-keys")), b => {
            b.SetApplicationName("Kairon"); if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi();
        });
        var service = new DatabaseConfigurationService(new(Path.Combine(paths.Config, "database-settings.protected")), diskProtection,
            new("SqlServer", "old"), probe, new Host(), NullLogger<DatabaseConfigurationService>.Instance);
        Assert.True((await service.SaveAsync(new(), default)).RequiresRestart);
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager(); config["Persistence:Provider"] = "SqlServer";
        await DatabaseConfigurationService.ApplySavedAtStartupAsync(config, paths, false);
        Assert.Equal("SQLite", config["Persistence:Provider"]); Assert.Equal(0, probe.Calls);
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    private sealed class Probe : IDatabaseConnectionProbe
    {
        public int Calls; public bool Success = true; public string Last = "";
        public Task<bool> TestAsync(string connectionString, CancellationToken ct) { Calls++; Last = connectionString; return Task.FromResult(Success); }
    }
    private sealed class Host : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Kairon";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
