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
