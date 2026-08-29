using System.Diagnostics;
using System.Net.Http;

namespace Kairon.Desktop;

public sealed record StartupFailure(string Stage, string Reason);

/// <summary>
/// Spawns one child process (backend or AI service) and waits for it to answer a real health
/// endpoint before considering it ready - never trusts "Process.Start() didn't throw" alone
/// (docs/DESKTOP_SHELL.md; this is exactly what Azzy's productization branch's
/// LocalAiProcessService still doesn't do). Owns graceful, idempotent shutdown of the process it
/// started.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly string _name;
    private Process? _process;
    private bool _stopped;

    public ManagedProcess(string name) => _name = name;

    public async Task<StartupFailure?> StartAndWaitHealthyAsync(
        ProcessStartInfo startInfo, Uri healthUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new StartupFailure(_name, $"Could not start: {ex.Message}");
        }

        if (_process is null)
            return new StartupFailure(_name, "Process.Start returned no process.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                return new StartupFailure(_name, $"Process exited early (code {_process.ExitCode}).");

            try
            {
                var response = await http.GetAsync(healthUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return null;
            }
            catch
            {
                // Not up yet - keep polling until the deadline.
            }

            await Task.Delay(500, cancellationToken);
        }

        return new StartupFailure(_name, $"Did not become healthy within {timeout.TotalSeconds:0}s.");
    }

    /// <summary>Graceful first (CloseMainWindow, for a console/service host this is a no-op but
    /// harmless), then a bounded kill of the whole process tree. Idempotent - safe to call more
    /// than once, including from a shutdown path racing a crash.</summary>
    public void Stop()
    {
        if (_stopped || _process is null) return;
        _stopped = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have already exited between the check and the call.
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
