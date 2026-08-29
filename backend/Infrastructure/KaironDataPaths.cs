using Kairon.Backend.Configuration;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// Resolves the local, mutable data layout under %LOCALAPPDATA%\Kairon (data/logs/config/cache/
/// backups) - or, when Persistence:DatabasePath is set explicitly, treats its parent directory as
/// the root instead. Ported from origin/main's KaironDataPaths.cs, renamed into this namespace.
/// </summary>
public sealed record KaironDataPaths(string Root, string Data, string Logs, string Config, string Cache,
    string Backups, bool ManagedLayout)
{
    public string DatabasePath => Path.Combine(Data, "kairon.db");

    public static KaironDataPaths Resolve(PersistenceOptions options)
    {
        var explicitDatabase = string.IsNullOrWhiteSpace(options.DatabasePath)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DatabasePath));
        var root = explicitDatabase is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairon")
            : Directory.GetParent(explicitDatabase)?.FullName
              ?? throw new InvalidOperationException("Persistence:DatabasePath must have a parent directory.");

        return new KaironDataPaths(
            root,
            explicitDatabase is null ? Path.Combine(root, "data") : root,
            Path.Combine(root, "logs"),
            Path.Combine(root, "config"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "backups"),
            explicitDatabase is null);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Data);
        if (!ManagedLayout) return;
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Backups);
    }
}
