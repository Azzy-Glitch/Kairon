using System.Data;
using System.Data.Common;
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
    public const int CurrentVersion = 1;

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
            // adopt it only after proving every table/column expected by the current EF model is
            // present. An incomplete or genuinely older schema fails safely and keeps its backup.
            if (version == 0)
            {
                await ValidateCurrentModelAsync(connection, transaction, cancellationToken);
                await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {CurrentVersion};", cancellationToken);
                version = CurrentVersion;
                _logger.LogInformation("Adopted verified SQLite 1.0.1 schema as local schema version {Version}", version);
            }

            // Future explicit migrations are applied one version at a time inside this transaction.
            // There are no post-1.0.1 schema changes in the current candidate.
            if (version != CurrentVersion)
                throw new InvalidOperationException($"No SQLite migration path exists from version {version}.");

            await ValidateCurrentModelAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task ValidateCurrentModelAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var entity in _db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(table)) continue;

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            var expected = entity.GetProperties()
                .Select(property => property.GetColumnName(store))
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = await ColumnsAsync(connection, transaction, table, cancellationToken);

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
