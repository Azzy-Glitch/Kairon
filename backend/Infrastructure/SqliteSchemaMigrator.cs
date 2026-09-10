using System.Data;
using System.Data.Common;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Kairon.Backend.Infrastructure;

public interface ILocalSchemaMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Versioned SQLite schema lifecycle for the local-first product. The 1.0.1 database created by
/// the former EnsureCreated path is adopted as version 1 only after its full EF column shape is
/// verified. Future releases add explicit, transactional steps here instead of silently assuming
/// an existing database matches the current model.
/// </summary>
public sealed class SqliteSchemaMigrator : ILocalSchemaMigrator
{
    public const int CurrentVersion = 6;

    private readonly AppDbContext _db;
    private readonly ILogger<SqliteSchemaMigrator> _logger;

    public SqliteSchemaMigrator(AppDbContext db, ILogger<SqliteSchemaMigrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.Database.IsSqlite())
            throw new InvalidOperationException("The local schema migrator can only run against SQLite.");

        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);

        try
        {
            var tableCount = await ScalarIntAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
                cancellationToken);

            if (tableCount == 0)
            {
                await _db.Database.EnsureCreatedAsync(cancellationToken);
                if (connection.State != ConnectionState.Open)
                    await connection.OpenAsync(cancellationToken);
            }

            var version = await ScalarIntAsync(connection, "PRAGMA user_version;", cancellationToken);
            if (version > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"SQLite schema version {version} is newer than this KAIRON build supports ({CurrentVersion}).");
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Version 0 is the released 1.0.1 EnsureCreated schema. It has no version marker, so
            // adopt it only after proving every table/column that schema actually shipped with is
            // present - not the full current model, which may since have grown new tables of its
            // own (see version 1 below). An incomplete or genuinely older schema fails safely and
            // keeps its backup.
            if (version == 0)
            {
                await ValidateModelAsync(connection, transaction, EntitiesAsOf(version: 1), cancellationToken, legacy: true);
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 1;", cancellationToken);
                version = 1;
                _logger.LogInformation("Adopted verified SQLite 1.0.1 schema as local schema version {Version}", version);
            }

            // Version 2: adds AiProviderConfigs (frontend AI Configuration panel) - the first
            // post-1.0.1 schema change. Each future change appends one more step exactly like this
            // one, applied transactionally and one version at a time.
            if (version == 1)
            {
                await ExecuteAsync(connection, transaction, CreateAiProviderConfigsTableSql, cancellationToken);
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 2;", cancellationToken);
                version = 2;
                _logger.LogInformation("Applied SQLite schema migration to local schema version {Version}: AiProviderConfigs", version);
            }

            // Version 3: adds AiProviderConfigs.Endpoint - lets a saved provider point at a
            // dedicated/regional URL (e.g. an Alibaba Model Studio Token Plan workspace) instead of
            // always using the provider's shared public endpoint.
            if (version == 2)
            {
                // ALTER TABLE ADD COLUMN has no IF NOT EXISTS in SQLite - a genuinely fresh install
                // already has this column (EnsureCreatedAsync built the table from the current
                // model), so guard with an explicit check the way CREATE TABLE's IF NOT EXISTS does.
                var columns = await ColumnsAsync(connection, transaction, "AiProviderConfigs", cancellationToken);
                if (!columns.Contains("Endpoint"))
                    await ExecuteAsync(connection, transaction, AddAiProviderConfigsEndpointColumnSql, cancellationToken);
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 3;", cancellationToken);
                version = 3;
                _logger.LogInformation("Applied SQLite schema migration to local schema version {Version}: AiProviderConfigs.Endpoint", version);
            }

            if (version == 3) {
                foreach (var table in new[] { "Incidents", "Metrics" }) {
                    var columns = await ColumnsAsync(connection, transaction, table, cancellationToken);
                    if (!columns.Contains("MachineId"))
                        await ExecuteAsync(connection, transaction, $"ALTER TABLE \"{table}\" ADD COLUMN \"MachineId\" TEXT NULL;", cancellationToken);
                }
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 4;", cancellationToken);
                version = 4;
            }

            // Version 5: adds RemediationTargets, the database-backed replacement for
            // WindowsRemediation:Targets (backend/Configuration/WindowsRemediationOptions.cs).
            // Mirrors AppDbContext's Fluent API config exactly, including the filtered unique
            // index (only Enabled targets need a unique ProjectId+Environment+Service identity -
            // SQLite supports a WHERE-filtered unique index natively).
            if (version == 4) {
                await ExecuteAsync(connection, transaction, CreateRemediationTargetsTableSql, cancellationToken);
                await ExecuteAsync(connection, transaction, CreateRemediationTargetsMachineIndexSql, cancellationToken);
                await ExecuteAsync(connection, transaction, CreateRemediationTargetsCredentialIndexSql, cancellationToken);
                await ExecuteAsync(connection, transaction, CreateRemediationTargetsUniqueIndexSql, cancellationToken);
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 5;", cancellationToken);
                version = 5;
                _logger.LogInformation("Applied SQLite schema migration to local schema version {Version}: RemediationTargets", version);
            }

            // Version 6: adds SdkPairingSessions.IssuedCredentialId/ConfirmedAt - the safe re-pair
            // handoff (backend/Services/SdkPairingService.cs). RemediationTarget.UpdatedAt also
            // becomes an EF concurrency token in this release, but that is a mapping-only change
            // (AppDbContext) with no schema effect, so nothing to migrate for it here.
            if (version == 5) {
                foreach (var column in new[] { "IssuedCredentialId", "ConfirmedAt" }) {
                    var columns = await ColumnsAsync(connection, transaction, "SdkPairingSessions", cancellationToken);
                    if (!columns.Contains(column))
                        await ExecuteAsync(connection, transaction, $"ALTER TABLE \"SdkPairingSessions\" ADD COLUMN \"{column}\" TEXT NULL;", cancellationToken);
                }
                await ExecuteAsync(connection, transaction, "PRAGMA user_version = 6;", cancellationToken);
                version = 6;
                _logger.LogInformation("Applied SQLite schema migration to local schema version {Version}: SdkPairingSessions confirmation columns", version);
            }

            if (version != CurrentVersion)
                throw new InvalidOperationException($"No SQLite migration path exists from version {version}.");

            await ValidateModelAsync(connection, transaction, _db.Model.GetEntityTypes(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    /// <summary>Entity types a given schema version is expected to have. Every version after the
    /// 1.0.1 baseline just adds to the full current model, so this only ever needs to name what to
    /// *exclude* for an older version - version 1 predates AiProviderConfig.</summary>
    private IEnumerable<IEntityType> EntitiesAsOf(int version) => version switch
    {
        1 => _db.Model.GetEntityTypes().Where(e => e.ClrType != typeof(AiProviderConfig)),
        _ => _db.Model.GetEntityTypes(),
    };

    private const string CreateAiProviderConfigsTableSql = """
        CREATE TABLE IF NOT EXISTS "AiProviderConfigs" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_AiProviderConfigs" PRIMARY KEY,
            "Provider" TEXT NOT NULL,
            "Model" TEXT NOT NULL,
            "EncryptedApiKey" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        """;

    private const string AddAiProviderConfigsEndpointColumnSql =
        """ALTER TABLE "AiProviderConfigs" ADD COLUMN "Endpoint" TEXT NOT NULL DEFAULT '';""";

    private const string CreateRemediationTargetsTableSql = """
        CREATE TABLE IF NOT EXISTS "RemediationTargets" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_RemediationTargets" PRIMARY KEY,
            "ProjectId" TEXT NOT NULL,
            "Environment" TEXT NOT NULL,
            "Service" TEXT NOT NULL,
            "MachineId" TEXT NOT NULL,
            "TelemetryCredentialId" TEXT NOT NULL,
            "ExpectedHostName" TEXT NOT NULL,
            "WindowsServiceName" TEXT NOT NULL,
            "AllowedOperationsJson" TEXT NOT NULL,
            "Enabled" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_RemediationTargets_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
        );
        """;

    private const string CreateRemediationTargetsMachineIndexSql =
        """CREATE INDEX IF NOT EXISTS "IX_RemediationTargets_MachineId" ON "RemediationTargets" ("MachineId");""";

    private const string CreateRemediationTargetsCredentialIndexSql =
        """CREATE INDEX IF NOT EXISTS "IX_RemediationTargets_TelemetryCredentialId" ON "RemediationTargets" ("TelemetryCredentialId");""";

    // Enabled-only uniqueness: a disabled target may coexist with its enabled replacement -
    // see AppDbContext's matching HasFilter("Enabled = 1") configuration.
    private const string CreateRemediationTargetsUniqueIndexSql =
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_RemediationTargets_ProjectId_Environment_Service" ON "RemediationTargets" ("ProjectId", "Environment", "Service") WHERE "Enabled" = 1;""";

    private async Task ValidateModelAsync(
        DbConnection connection,
        DbTransaction transaction,
        IEnumerable<IEntityType> entities,
        CancellationToken cancellationToken, bool legacy = false)
    {
        foreach (var entity in entities)
        {
            var table = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(table)) continue;

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var expected = entity.GetProperties()
                .Select(property => property.GetColumnName(store))
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = await ColumnsAsync(connection, transaction, table, cancellationToken);

            if (legacy && table is "Incidents" or "Metrics") expected.Remove("MachineId");
            var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (actual.Count == 0 || missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"SQLite schema validation failed for table '{table}'. Missing columns: " +
                    (missing.Length == 0 ? "<table missing>" : string.Join(", ", missing)));
            }
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<int> ScalarIntAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
