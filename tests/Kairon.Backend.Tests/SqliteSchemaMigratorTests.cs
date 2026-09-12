using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class SqliteSchemaMigratorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"kairon-schema-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task FreshDatabaseIsCreatedAndVersioned()
    {
        await using var db = CreateContext();
        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.True(await db.Projects.AnyAsync() == false);
    }

    /// <summary>Covers a database that already happens to have the CURRENT shape but no version
    /// marker (e.g. built by dev/test tooling via EnsureCreatedAsync). This is NOT a substitute for
    /// testing a genuine legacy database - see
    /// GenuineLegacyVersionZeroDatabaseUpgradesThroughAllMigrationsWithoutLosingData below for the
    /// real 1.0.1 shape, which is missing tables/columns this fixture already has.</summary>
    [Fact]
    public async Task ExistingEnsureCreatedDatabaseIsAdoptedWithoutLosingData()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-me" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-me", (await db.Projects.SingleAsync()).Name);
    }

    [Fact]
    public async Task ExistingVersion1DatabaseGainsAiProviderConfigsWithoutLosingData()
    {
        // Simulates a genuine upgrade: a database already adopted as schema version 1, from
        // before AiProviderConfigs existed - not just "EnsureCreatedAsync happened to include it
        // already", which every other test here would otherwise mask.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-me-too" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AiProviderConfigs\";");
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 1;");

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-me-too", (await db.Projects.SingleAsync()).Name);
        Assert.True(await db.AiProviderConfigs.AnyAsync() == false);
    }

    [Fact]
    public async Task ExistingVersion2DatabaseGainsEndpointColumnWithoutLosingData()
    {
        // Simulates a genuine upgrade from a database already adopted as schema version 2 - it has
        // AiProviderConfigs, but from before the Endpoint column existed - not just
        // "EnsureCreatedAsync happened to include it already", which every other test here would
        // otherwise mask.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-me-three" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AiProviderConfigs\";");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "AiProviderConfigs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AiProviderConfigs" PRIMARY KEY,
                "Provider" TEXT NOT NULL,
                "Model" TEXT NOT NULL,
                "EncryptedApiKey" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2;");

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-me-three", (await db.Projects.SingleAsync()).Name);

        // Proves the new column is genuinely usable, not just present - EF would fail to write a
        // row at all if the migration had left the schema out of sync with the current model.
        db.AiProviderConfigs.Add(new AiProviderConfig
        {
            Provider = "qwen", Model = "qwen-plus",
            Endpoint = "https://ws-example.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions",
            EncryptedApiKey = "cipher",
        });
        await db.SaveChangesAsync();
        Assert.Equal(
            "https://ws-example.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions",
            (await db.AiProviderConfigs.SingleAsync()).Endpoint);
    }

    [Fact]
    public async Task GenuineLegacyVersionZeroDatabaseUpgradesThroughAllMigrationsWithoutLosingData()
    {
        // Reconstructs the actual 1.0.1 shipped schema by starting from EnsureCreatedAsync's
        // current-model output and stripping exactly what each later versioned migration step (2
        // through 7) is the one that introduces - rather than pretending the CURRENT model IS what
        // 1.0.1 shipped with. That distinction is the entire point of this test: adopting version 0
        // used to validate against nearly the full current model (see SqliteSchemaMigrator's fixed
        // EntitiesAsOf/ValidateModelAsync), so a genuine legacy database - missing RemediationTargets
        // and four SdkPairingSessions columns that don't exist until version 5/6/7 - could never
        // actually be adopted, even though ExistingEnsureCreatedDatabaseIsAdoptedWithoutLosingData
        // above happened to pass regardless, because it never removes what EnsureCreatedAsync
        // (using the CURRENT model) already includes.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        var project = new Project { Name = "legacy-preserve-me" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AiProviderConfigs\";"); // version 2
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Incidents\" DROP COLUMN \"MachineId\";"); // version 4
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Metrics\" DROP COLUMN \"MachineId\";"); // version 4
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RemediationTargets\";"); // version 5
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"IssuedCredentialId\";"); // version 6
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ConfirmedAt\";"); // version 6
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ReplacesCredentialId\";"); // version 7
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"CompletedAt\";"); // version 7
        // user_version is already 0 by default here - EnsureCreatedAsync never touches the PRAGMA.

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("legacy-preserve-me", (await db.Projects.SingleAsync()).Name);

        // The new schema must be genuinely usable after upgrading from a real legacy database, not
        // merely present - write a real row through every column the upgrade path is supposed to
        // have added.
        var machine = new Machine { HostName = "legacy-host", OperatingSystem = "Windows", AgentCredentialHash = "h", LastSeenAt = DateTime.UtcNow };
        db.Machines.Add(machine);
        var credential = new ProjectApiCredential { ProjectId = project.Id, KeyHash = "k" };
        db.ProjectApiCredentials.Add(credential);
        await db.SaveChangesAsync();

        db.RemediationTargets.Add(new RemediationTarget
        {
            ProjectId = project.Id, Environment = "Production", Service = "svc", MachineId = machine.Id,
            TelemetryCredentialId = credential.Id, ExpectedHostName = "legacy-host", WindowsServiceName = "Svc",
            AllowedOperationsJson = "[]", Enabled = true
        });
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.RemediationTargets.CountAsync());

        var session = new SdkPairingSession
        {
            ProjectId = project.Id, SdkType = "python", CodeHash = "hash", ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            ReplacesCredentialId = credential.Id
        };
        db.SdkPairingSessions.Add(session);
        await db.SaveChangesAsync();
        session.IssuedCredentialId = credential.Id;
        session.ConfirmedAt = DateTime.UtcNow;
        session.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); // would fail if any of these columns weren't genuinely present/usable
    }

    [Fact]
    public async Task ExistingVersion4DatabaseGainsRemediationTargetsAndPairingConfirmationWithoutLosingData()
    {
        // Simulates a genuine upgrade from a database already adopted as schema version 4 - before
        // RemediationTargets and the pairing-confirmation/repair-binding columns existed.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-v4" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RemediationTargets\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"IssuedCredentialId\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ConfirmedAt\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ReplacesCredentialId\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"CompletedAt\";");
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 4;");

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-v4", (await db.Projects.SingleAsync()).Name);
        Assert.True(await db.RemediationTargets.AnyAsync() == false);
    }

    [Fact]
    public async Task ExistingVersion5DatabaseGainsPairingConfirmationAndRepairBindingWithoutLosingData()
    {
        // Simulates a genuine upgrade from a database already adopted as schema version 5 - it has
        // RemediationTargets, but from before any of the pairing-confirmation/repair-binding/
        // concurrency-token columns existed.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-v5" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"IssuedCredentialId\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ConfirmedAt\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ReplacesCredentialId\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"CompletedAt\";");
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 5;");

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-v5", (await db.Projects.SingleAsync()).Name);
    }

    [Fact]
    public async Task ExistingVersion6DatabaseGainsRepairBindingAndConcurrencyTokenWithoutLosingData()
    {
        // Simulates a genuine upgrade from a database already adopted as schema version 6 - it has
        // pairing confirmation, but from before ReplacesCredentialId/CompletedAt/RowVersion existed.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "preserve-v6" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"ReplacesCredentialId\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SdkPairingSessions\" DROP COLUMN \"CompletedAt\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"RemediationTargets\" DROP COLUMN \"RowVersion\";");
        await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 6;");

        await new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync();

        Assert.Equal(SqliteSchemaMigrator.CurrentVersion, await UserVersionAsync(db));
        Assert.Equal("preserve-v6", (await db.Projects.SingleAsync()).Name);
    }

    [Fact]
    public async Task NewerUnknownSchemaFailsClosed()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            $"PRAGMA user_version = {SqliteSchemaMigrator.CurrentVersion + 1};");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync());

        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompleteUnversionedSchemaIsNeverSilentlyAdopted()
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE Projects (Id TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteSchemaMigrator(db, NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync());

        Assert.Contains("schema validation failed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<int> UserVersionAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
