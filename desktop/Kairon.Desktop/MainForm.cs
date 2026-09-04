using System.Diagnostics;
using System.Security.Cryptography;
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

    // Created before either child process starts, so both are bound to it from the moment they
    // exist (see ChildProcessJob's own doc comment for why this matters beyond the graceful path
    // below).
    private readonly ChildProcessJob _childProcessJob = new();
    private readonly ManagedProcess _backend;
    private readonly ManagedProcess _ai;
    private readonly string _operatorKey = CreateEphemeralKey();
    private readonly string _aiApiKey = CreateEphemeralKey();
    private WebView2? _webView;
    private bool _shuttingDown;

    public MainForm()
    {
        _backend = new ManagedProcess("Backend", _childProcessJob);
        _ai = new ManagedProcess("AI service", _childProcessJob);

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

        // Creating the WebView2 environment (spinning up the Edge runtime against our user-data
        // folder) needs neither child process to be up - only the final Navigate does. Starting it
        // here overlaps it with backend/AI startup instead of paying for it afterwards, and changes
        // no startup or failure behaviour: the awaits below still gate in exactly the same order.
        var webViewEnvironment = CreateWebViewEnvironmentAsync();

        var backendFailure = await StartBackendAsync(cts.Token);
        if (backendFailure is not null) { ShowStartupFailure(backendFailure); return; }

        _statusLabel.Text = "Backend ready. Starting AI service...";

        var aiFailure = await StartAiAsync(cts.Token);
        if (aiFailure is not null) { ShowStartupFailure(aiFailure); return; }

        _statusLabel.Text = "Loading Kairon...";
        await ShowWebViewAsync(webViewEnvironment);
    }

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateWebViewEnvironmentAsync() =>
        Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            userDataFolder: AppPaths.WebView2DataDirectory());

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

        startInfo.Environment["SreSecurity__RequireOperatorKey"] = "true";
        startInfo.Environment["SreSecurity__OperatorKey"] = _operatorKey;
        startInfo.Environment["AiService__ApiKey"] = _aiApiKey;

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

        startInfo.Environment["KAIRON_AI_API_KEY"] = _aiApiKey;

        return _ai.StartAndWaitHealthyAsync(startInfo, new Uri($"{AiUrl}/health"),
            TimeSpan.FromSeconds(30), cancellationToken);
    }

    private async Task ShowWebViewAsync(
        Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> environmentTask)
    {
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        // Usually already finished by now - it has been running since StartupAsync began.
        var environment = await environmentTask;
        await _webView.EnsureCoreWebView2Async(environment);

        // Bound only disposable browser cache. Cookies/local storage/settings are preserved, and
        // failure to inspect or clear cache never prevents the product from starting.
        if (AppPaths.WebView2DataExceedsLimit())
        {
            try
            {
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache);
            }
            catch (Exception ex)
            {
                AppPaths.AppendStartupLog(
                    $"{DateTime.Now:HH:mm:ss.fff} WebView2 cache cleanup skipped - {ex.GetType().Name}");
            }
        }

        // The key stays in the native host. It is attached at the WebView network boundary and is
        // never embedded in JavaScript, local storage, a URL, or a checked-in configuration file.
        _webView.CoreWebView2.AddWebResourceRequestedFilter(
            $"{BackendUrl}/api/*",
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        _webView.CoreWebView2.WebResourceRequested += (_, args) =>
            args.Request.Headers.SetHeader("X-Kairon-Operator-Key", _operatorKey);

        Controls.Remove(_statusLabel);
        _webView.CoreWebView2.Navigate(BackendUrl);
    }

    /// <summary>Stops both children concurrently rather than one after the other. They are entirely
    /// independent processes with no shared state and no ordering requirement between them, so
    /// serializing their teardown only meant paying each one's grace window twice on every close.
    /// Still fully synchronous from the caller's point of view: FormClosing must not return until
    /// both are actually down.</summary>
    private void StopChildProcesses() =>
        Task.WaitAll(Task.Run(_backend.Stop), Task.Run(_ai.Stop));

    private static string CreateEphemeralKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private void ShowStartupFailure(StartupFailure failure)
    {
        // The dialog below says "see Kairon logs for details" - this is what makes that true.
        try
        {
            AppPaths.AppendStartupLog(
                $"{DateTime.Now:HH:mm:ss.fff} startup failed - stage={failure.Stage} reason={failure.Reason}");
        }
        catch
        {
            // Logging the failure must never itself block reporting the failure to the user.
        }

        MessageBox.Show(
            $"Kairon could not start.\n\nStage:\n{failure.Stage}\n\nReason:\n{failure.Reason}\n\nSee Kairon logs for details.",
            "Kairon", MessageBoxButtons.OK, MessageBoxIcon.Error);

        StopChildProcesses();
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Idempotent: FormClosing can fire more than once on some shutdown paths (e.g. a failed
        // startup already called Close(), which re-enters here) - only tear down once.
        if (_shuttingDown) return;
        _shuttingDown = true;

        // Root cause of the "Backend/AI stay alive after close" defect: this used to dispose the
        // WebView2 control BEFORE stopping the child processes. WebView2 disposal from inside
        // FormClosing is a known source of exceptions/hangs in its own SDK (it is tearing down an
        // active CoreWebView2 environment with in-flight requests, including the very
        // WebResourceRequested handler wired up in ShowWebViewAsync) - when that throws, everything
        // after it in this handler, including _backend.Stop()/_ai.Stop(), never ran, leaving both
        // child processes (and the ports they hold) orphaned.
        //
        // Fixed two ways, deliberately redundant:
        //  1. The two calls that actually matter run first, wrapped so nothing after them can stop
        //     them from completing.
        //  2. ChildProcessJob is the OS-level backstop for every OTHER way this process can end
        //     (a crash, a forced kill, a system shutdown) - closing it here also covers the
        //     graceful path in case Stop() itself ever fails for some other reason.
        try
        {
            StopChildProcesses();
        }
        finally
        {
            _childProcessJob.Dispose();
        }

        // Best-effort and last: a WebView2 disposal failure is now cosmetic (a possible dangling
        // browser process/dialog), never a reason the backend/AI stay up.
        try
        {
            _webView?.Dispose();
        }
        catch (Exception ex)
        {
            try { AppPaths.AppendStartupLog($"{DateTime.Now:HH:mm:ss.fff} WebView2 disposal on close failed - {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging the failure must never block shutdown */ }
        }
    }
}
