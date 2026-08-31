using System.Diagnostics;
using Microsoft.Web.WebView2.WinForms;

namespace Kairon.Desktop;

/// <summary>
/// The native window: starts the backend and AI service as child processes, waits for both to
/// report real health, then hosts the existing React UI in a WebView2 control. Never opens a
/// system browser - that was the actual bug in the original --desktop flag (Azzy's
/// productization branch's ProductDashboardLaunchService just does
/// Process.Start(url, UseShellExecute=true), i.e. it opens whatever the default browser is).
/// </summary>
public sealed class MainForm : Form
{
    private const string BackendUrl = "http://127.0.0.1:8000";
    private const string AiUrl = "http://127.0.0.1:8001";

    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 12F),
        Text = "Starting Kairon..."
    };

    private readonly ManagedProcess _backend = new("Backend");
    private readonly ManagedProcess _ai = new("AI service");
    private WebView2? _webView;
    private bool _shuttingDown;

    public MainForm()
    {
        Text = "Kairon";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_statusLabel);

        FormClosing += OnFormClosing;
        Shown += async (_, _) => await StartupAsync();
    }

    private async Task StartupAsync()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var backendFailure = await StartBackendAsync(cts.Token);
        if (backendFailure is not null) { ShowStartupFailure(backendFailure); return; }

        _statusLabel.Text = "Backend ready. Starting AI service...";

        var aiFailure = await StartAiAsync(cts.Token);
        if (aiFailure is not null) { ShowStartupFailure(aiFailure); return; }

        _statusLabel.Text = "Loading Kairon...";
        await ShowWebViewAsync();
    }

    private Task<StartupFailure?> StartBackendAsync(CancellationToken cancellationToken)
    {
        var publishedExe = AppPaths.FindPublishedBackendExe();
        var publishedDll = AppPaths.FindPublishedBackendDll();
        ProcessStartInfo startInfo;

        if (publishedExe is not null)
        {
            // The self-contained apphost - runs directly with its own bundled runtime, no
            // globally-installed `dotnet` needed on the target machine (docs/DESKTOP_SHELL.md).
            startInfo = new ProcessStartInfo(publishedExe, $"--urls {BackendUrl}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(publishedExe)!
            };
        }
        else if (publishedDll is not null)
        {
            // Fallback for a framework-dependent publish (no self-contained apphost present) -
            // this product doesn't ship this way today, but a `dotnet` on PATH still works if it
            // ever does.
            startInfo = new ProcessStartInfo("dotnet", $"\"{publishedDll}\" --urls {BackendUrl}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(publishedDll)!
            };
        }
        else
        {
            var devProject = AppPaths.FindDevBackendProject();
            if (devProject is null)
                return Task.FromResult<StartupFailure?>(new StartupFailure("Backend", "Could not locate a published or dev-source backend."));

            // "--" separates dotnet run's own arguments from the ones forwarded to the started
            // app; without it, --urls is ambiguous with dotnet run's own option parser.
            startInfo = new ProcessStartInfo("dotnet", $"run --project \"{devProject}\" -- --urls {BackendUrl}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(devProject)!
            };
        }

        return _backend.StartAndWaitHealthyAsync(startInfo, new Uri($"{BackendUrl}/api/health"),
            TimeSpan.FromSeconds(45), cancellationToken);
    }

    private Task<StartupFailure?> StartAiAsync(CancellationToken cancellationToken)
    {
        var publishedExe = AppPaths.FindPublishedAiExecutable();
        ProcessStartInfo startInfo;

        if (publishedExe is not null)
        {
            startInfo = new ProcessStartInfo(publishedExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(publishedExe)!
            };
        }
        else
        {
            var devDir = AppPaths.FindDevAiServiceDirectory();
            if (devDir is null)
                return Task.FromResult<StartupFailure?>(new StartupFailure("AI service", "Could not locate a published or dev-source AI service."));

            startInfo = new ProcessStartInfo("python", "-m uvicorn main:app --port 8001")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = devDir
            };
        }

        return _ai.StartAndWaitHealthyAsync(startInfo, new Uri($"{AiUrl}/health"),
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    private async Task ShowWebViewAsync()
    {
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        var userDataFolder = Path.Combine(AppPaths.LocalDataLogsDirectory(), "..", "webview2");
        var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.GetFullPath(userDataFolder));
        await _webView.EnsureCoreWebView2Async(environment);

        Controls.Remove(_statusLabel);
        _webView.CoreWebView2.Navigate(BackendUrl);
    }

    private void ShowStartupFailure(StartupFailure failure)
    {
        // The dialog below says "see Kairon logs for details" - this is what makes that true.
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.LocalDataLogsDirectory(), "startup.log"),
                $"{DateTime.Now:HH:mm:ss.fff} startup failed - stage={failure.Stage} reason={failure.Reason}\n");
        }
        catch
        {
            // Logging the failure must never itself block reporting the failure to the user.
        }

        MessageBox.Show(
            $"Kairon could not start.\n\nStage:\n{failure.Stage}\n\nReason:\n{failure.Reason}\n\nSee Kairon logs for details.",
            "Kairon", MessageBoxButtons.OK, MessageBoxIcon.Error);

        _backend.Stop();
        _ai.Stop();
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Idempotent: FormClosing can fire more than once on some shutdown paths (e.g. a failed
        // startup already called Close(), which re-enters here) - only tear down once.
        if (_shuttingDown) return;
        _shuttingDown = true;

        _webView?.Dispose();
        _backend.Stop();
        _ai.Stop();
    }
}
