using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace Kairon.Backend.Services;

public sealed class DatabaseSettingsValidationException(string message) : Exception(message);

public sealed class DatabaseSettingsRequest
{
    public string Provider { get; set; } = "SQLite";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Authentication { get; set; } = "Windows";
    public string UserName { get; set; } = "";
    public string? Password { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
}

public sealed record DatabaseSettingsView(string Provider, string Server, string Database, string Authentication,
    string UserName, bool HasPassword, bool Encrypt, bool TrustServerCertificate);
public sealed record DatabaseSettingsSummary(string ActiveProvider, DatabaseSettingsView Selected, bool RequiresRestart, string ApplyMode = "restart");
public sealed record DatabaseConnectionResult(bool Success, string Message);
public sealed record DatabaseConfigurationLocation(string FilePath);
public sealed record DatabaseRuntimeSelection(string Provider, string Signature);

public interface IDatabaseConnectionProbe
{
    Task<bool> TestAsync(string connectionString, CancellationToken ct);
}

public sealed class SqlServerConnectionProbe : IDatabaseConnectionProbe
{
    public async Task<bool> TestAsync(string connectionString, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5, Pooling = false };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(timeout.Token); // Opens the specified existing database; never creates one.
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1"; command.CommandTimeout = 5;
            return Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; } // Never return or log provider errors/connection objects containing credentials.
    }
}

/// <summary>Encrypted bootstrap configuration outside the selected database, using KAIRON's existing key ring.</summary>
public sealed class DatabaseConfigurationService(DatabaseConfigurationLocation location, IDataProtectionProvider protection,
    DatabaseRuntimeSelection active, IDatabaseConnectionProbe probe, IHostEnvironment environment,
    ILogger<DatabaseConfigurationService> logger)
{
    public const string Purpose = "Kairon.DatabaseConfiguration.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);
    private readonly SemaphoreSlim _writes = new(1, 1);

    public DatabaseSettingsSummary Get()
    {
        var saved = Read(location.FilePath, _protector);
        var selected = saved ?? new DatabaseSettingsRequest { Provider = active.Provider };
        return Summary(selected, saved is not null && Signature(saved) != active.Signature);
    }

    public async Task<DatabaseConnectionResult> TestAsync(DatabaseSettingsRequest request, CancellationToken ct)
    {
        var candidate = Resolve(request);
        if (candidate.Provider == "SQLite")
            return new(true, "SQLite uses KAIRON's local database file and requires no external server.");
        var ok = await probe.TestAsync(ConnectionString(candidate), ct);
        return new(ok, ok ? "Connected to the existing SQL Server database. Schema permissions are checked when the backend starts."
            : "Cannot connect to the database. Check the server, database, credentials, network and certificate settings.");
    }

    public async Task<DatabaseSettingsSummary> SaveAsync(DatabaseSettingsRequest request, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var candidate = Resolve(request);
            if (candidate.Provider == "SqlServer" && !await probe.TestAsync(ConnectionString(candidate), ct))
                throw new InvalidOperationException("The existing SQL Server database could not be reached. Configuration was not saved.");
            var encrypted = _protector.Protect(JsonSerializer.Serialize(candidate));
            var folder = Path.GetDirectoryName(location.FilePath)!;
            Directory.CreateDirectory(folder);
            var temporary = location.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, encrypted, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, location.FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            logger.LogInformation("Saved database settings for backend restart: provider={Provider}", candidate.Provider);
            return Summary(candidate, Signature(candidate) != active.Signature);
        }
        finally { _writes.Release(); }
    }

    private DatabaseSettingsSummary Summary(DatabaseSettingsRequest s, bool restart) => new(active.Provider,
        new(s.Provider, s.Server, s.Database, s.Authentication, s.UserName, !string.IsNullOrEmpty(s.Password), s.Encrypt, s.TrustServerCertificate), restart);

    private DatabaseSettingsRequest Resolve(DatabaseSettingsRequest input)
    {
        var candidate = Normalize(input, environment.IsProduction());
        if (candidate.Provider == "SqlServer" && candidate.Authentication == "SqlLogin" && string.IsNullOrEmpty(candidate.Password))
        {
            var saved = Read(location.FilePath, _protector);
            // Never send a stored password to a newly entered target, principal or downgraded TLS setting.
            if (saved is not null && SameSqlTarget(saved, candidate)) candidate.Password = saved.Password;
            if (string.IsNullOrEmpty(candidate.Password)) throw new DatabaseSettingsValidationException("Enter a password for this SQL Server connection.");
        }
        return candidate;
    }

    public static DatabaseSettingsRequest Normalize(DatabaseSettingsRequest input, bool production)
    {
        if (string.Equals(input.Provider, "SQLite", StringComparison.OrdinalIgnoreCase)) return new();
        if (!string.Equals(input.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase)) throw new DatabaseSettingsValidationException("Select SQLite or SQL Server.");
        var server = (input.Server ?? "").Trim(); var database = (input.Database ?? "").Trim(); var user = (input.UserName ?? "").Trim();
        if (server.Length is < 1 or > 255 || database.Length is < 1 or > 128) throw new DatabaseSettingsValidationException("Enter a server and an existing database name.");
        if (input.Authentication is not ("Windows" or "SqlLogin")) throw new DatabaseSettingsValidationException("Select Windows authentication or SQL Server login.");
        if (input.Authentication == "SqlLogin" && (user.Length is < 1 or > 128)) throw new DatabaseSettingsValidationException("Enter a SQL Server user name.");
        if ((input.Password?.Length ?? 0) > 1024) throw new DatabaseSettingsValidationException("The database password is too long.");
        if (production && (!input.Encrypt || input.TrustServerCertificate)) throw new DatabaseSettingsValidationException("Production SQL Server connections require encryption and a validated server certificate.");
        return new() { Provider = "SqlServer", Server = server, Database = database, Authentication = input.Authentication,
            UserName = input.Authentication == "Windows" ? "" : user, Password = input.Authentication == "Windows" ? null : input.Password,
            Encrypt = input.Encrypt, TrustServerCertificate = input.TrustServerCertificate };
    }
    private static bool SameSqlTarget(DatabaseSettingsRequest a, DatabaseSettingsRequest b) =>
        a.Provider == b.Provider && a.Server == b.Server && a.Database == b.Database && a.Authentication == b.Authentication &&
        a.UserName == b.UserName && a.Encrypt == b.Encrypt && a.TrustServerCertificate == b.TrustServerCertificate;

    public static string ConnectionString(DatabaseSettingsRequest s) => new SqlConnectionStringBuilder {
        DataSource = s.Server, InitialCatalog = s.Database, IntegratedSecurity = s.Authentication == "Windows",
        UserID = s.Authentication == "SqlLogin" ? s.UserName : "", Password = s.Authentication == "SqlLogin" ? s.Password ?? "" : "",
        Encrypt = s.Encrypt, TrustServerCertificate = s.TrustServerCertificate, PersistSecurityInfo = false,
        ConnectTimeout = 15, ApplicationName = "KAIRON"
    }.ConnectionString;

    public static string Signature(DatabaseSettingsRequest s) => s.Provider == "SQLite" ? "SQLite" : ConnectionSignature(ConnectionString(s));
    public static string ConnectionSignature(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static DatabaseSettingsRequest? Read(string path, IDataProtector protector)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
            return JsonSerializer.Deserialize<DatabaseSettingsRequest>(protector.Unprotect(File.ReadAllText(path)))
                ?? throw new InvalidDataException();
        }
        catch { throw new InvalidOperationException("Saved database configuration cannot be read. Restore its Data Protection keys or move the saved database-settings file aside to use deployment defaults."); }
    }

    public static async Task ApplySavedAtStartupAsync(ConfigurationManager configuration, KaironDataPaths paths, bool production)
    {
        var path = Path.Combine(paths.Config, "database-settings.protected");
        if (!File.Exists(path)) return; // Untouched installations keep SQLite/default configuration and need no SQL infrastructure.
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(paths.Config, "dataprotection-keys")), b => {
            b.SetApplicationName("Kairon"); if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi();
        });
        var saved = Normalize(Read(path, protection.CreateProtector(Purpose))!, production);
        var settings = new Dictionary<string, string?> { ["Persistence:Provider"] = saved.Provider };
        if (saved.Provider == "SqlServer")
        {
            var connection = ConnectionString(saved);
            if (!await new SqlServerConnectionProbe().TestAsync(connection, CancellationToken.None))
                throw new InvalidOperationException("The saved SQL Server database is unavailable. No database was created and SQLite fallback was not performed.");
            settings["ConnectionStrings:DefaultConnection"] = connection;
        }
        configuration.AddInMemoryCollection(settings);
    }
}
