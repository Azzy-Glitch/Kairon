namespace Kairon.Desktop;

/// <summary>
/// Locates the backend and AI service to launch. Supports two layouts so the same shell works
/// both during development (running straight from source, no publish/install step) and once
/// installed (Phase 6's installer, adapted from Azzy's productization branch):
///
///   Installed:  Program Files\Kairon\Kairon.exe
///               Program Files\Kairon\backend\Kairon.Backend.dll
///               Program Files\Kairon\ai\Kairon.AI.exe        (PyInstaller onefile, if built)
///
///   Dev source: desktop\Kairon.Desktop\bin\...\Kairon.exe
///               backend\Kairon.Backend.csproj  (found by walking up to the Kairon.slnx root)
///               ai-service\main.py
/// </summary>
public static class AppPaths
{
    private const long MaximumStartupLogBytes = 1024 * 1024;
    private const int RetainedCrashLogCount = 20;
    public const long MaximumWebView2CacheBytes = 100L * 1024 * 1024;
    private static readonly object LogSync = new();

    /// <summary>The real, self-contained apphost - published with --self-contained true -r
    /// win-x64 (installer/build-installer.ps1), it bundles its own .NET runtime and runs directly,
    /// no globally-installed `dotnet` required on the target machine. Preferred over
    /// FindPublishedBackendDll, which requires shelling out to `dotnet` - that path only exists
    /// for a framework-dependent publish, which this product does not ship.</summary>
    public static string? FindPublishedBackendExe()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "backend", "Kairon.Backend.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public static string? FindPublishedBackendDll()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "backend", "Kairon.Backend.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    public static string? FindPublishedAiExecutable()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "ai", "Kairon.AI.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Walks up from the running executable looking for Kairon.slnx - the repo root
    /// marker - so dev-mode launches work regardless of Debug/Release or which machine built
    /// this. Returns null if not found (e.g. a genuinely standalone install missing the
    /// published backend/ai folders too - the caller surfaces that as a startup error).</summary>
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kairon.slnx")))
                return dir.FullName;
        }
        return null;
    }

    public static string? FindDevBackendProject()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var csproj = Path.Combine(root, "backend", "Kairon.Backend.csproj");
        return File.Exists(csproj) ? csproj : null;
    }

    public static string? FindDevAiServiceDirectory()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var mainPy = Path.Combine(root, "ai-service", "main.py");
        return File.Exists(mainPy) ? Path.Combine(root, "ai-service") : null;
    }

    public static string LocalDataLogsDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairon", "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string WebView2DataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairon", "webview2");

    public static bool WebView2DataExceedsLimit()
    {
        try
        {
            var directory = new DirectoryInfo(WebView2DataDirectory());
            if (!directory.Exists) return false;

            long total = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += file.Length;
                if (total > MaximumWebView2CacheBytes) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    public static void AppendStartupLog(string message)
    {
        lock (LogSync)
        {
            var path = Path.Combine(LocalDataLogsDirectory(), "startup.log");
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (stream.Length >= MaximumStartupLogBytes)
                stream.SetLength(0);
            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(message);
        }
    }

    public static void WriteCrashLog(Exception? exception)
    {
        var directory = LocalDataLogsDirectory();
        var path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.txt");
        File.WriteAllText(path, exception?.ToString() ?? "Unknown error.");

        foreach (var stale in new DirectoryInfo(directory)
                     .EnumerateFiles("crash-*.txt")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(RetainedCrashLogCount))
        {
            try { stale.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
