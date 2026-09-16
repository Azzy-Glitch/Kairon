using Microsoft.Data.SqlClient;

namespace Kairon.Backend.Tests;

/// <summary>
/// Whether a SQL Server instance these tests may create throwaway databases on is actually
/// reachable - decided by CONNECTING once, not by inferring it from the operating system.
///
/// "Windows implies LocalDB" was the previous rule, and it is simply untrue: a Windows developer
/// machine without SQL Server Express LocalDB installed made every [SqlServerFact] FAIL with
/// "Unable to locate a Local Database Runtime installation" rather than skip. A test that cannot
/// tell "this environment has no SQL Server" from "the migration is broken" is worse than no test,
/// because the noise trains people to ignore it.
///
/// The probe runs at most once per test run and its result is cached, so discovery pays a single
/// short connection attempt.
/// </summary>
internal static class SqlServerAvailability
{
    /// <summary>Default target: the same LocalDB instance SqlServerMigrationTests uses. Overridden
    /// by KAIRON_TEST_SQLSERVER (see .env.example), which points at a real instance instead.</summary>
    private const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private static readonly Lazy<string?> Probe = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when SQL Server is usable; otherwise the reason to skip, naming what was tried
    /// so a skipped run is self-explanatory.</summary>
    internal static string? SkipReason => Probe.Value;

    private static string? Detect()
    {
        var configured = Environment.GetEnvironmentVariable("KAIRON_TEST_SQLSERVER");
        var target = string.IsNullOrWhiteSpace(configured) ? LocalDb : configured;

        try
        {
            // "master" only, and read nothing: this asks whether the instance answers, and must
            // never touch or create anything on it.
            var builder = new SqlConnectionStringBuilder(target)
            {
                InitialCatalog = "master",
                ConnectTimeout = 15
            };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception exception)
        {
            var source = string.IsNullOrWhiteSpace(configured)
                ? @"(localdb)\MSSQLLocalDB"
                : "KAIRON_TEST_SQLSERVER";
            return $"No reachable SQL Server for these tests ({source}): {exception.Message.Split('\n')[0].Trim()}";
        }
    }
}
