using Microsoft.Win32;

namespace Kairon.Desktop;

/// <summary>
/// Installed mode: the backend runs as the Kairon.Backend Windows service (NT SERVICE\Kairon.Backend),
/// which also supervises the AI service. The desktop shell no longer starts, owns or stops either
/// process - it waits for the service to be healthy, reads the operator key the service publishes
/// on every start, and hosts the UI. A source checkout without the service keeps the original
/// child-process mode (see MainForm).
/// </summary>
public static class BackendService
{
    public const string ServiceName = "Kairon.Backend";

    public static bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key is not null;
    }

    public static string OperatorKeyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kairon", "backend", "operator", "operator.key");

    /// <summary>Polls the service's health endpoint until it answers, or reports why it did not.
    /// A freshly booted machine may still be starting the service, hence the generous window.</summary>
    public static async Task<StartupFailure?> WaitUntilHealthyAsync(Uri health, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(health, cancellationToken);
                if (response.IsSuccessStatusCode) return null;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return new StartupFailure("Backend service",
            "The Kairon Backend Windows service did not respond. Open Services (services.msc), start " +
            "\"Kairon Backend\", or restart Windows; its logs are in %ProgramData%\\Kairon\\backend\\logs.");
    }
}

/// <summary>Reads the operator key the service publishes, re-reading only when the file changes
/// (a service restart rotates it), so an already-open window keeps working across restarts. The key
/// stays in this process and is attached only at the WebView2 network boundary.</summary>
public sealed class OperatorKeySource
{
    private readonly string _path;
    private readonly object _gate = new();
    private DateTime _stamp;
    private string? _key;

    public OperatorKeySource(string path) => _path = path;

    public string? Current
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(_path);
                    if (_key is null || stamp != _stamp)
                    {
                        var text = File.ReadAllText(_path).Trim();
                        if (text.Length > 0) { _key = text; _stamp = stamp; }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return _key;
            }
        }
    }

    public async Task<bool> WaitAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Current is null)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(250, cancellationToken);
        }
        return true;
    }
}
