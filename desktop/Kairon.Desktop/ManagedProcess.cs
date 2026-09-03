using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

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
    private readonly ChildProcessJob? _job;
    private Process? _process;
    private bool _stopped;

    /// <param name="job">Optional OS-level safety net (ChildProcessJob) - when supplied, the
    /// spawned process is bound to it so Windows kills it automatically if Kairon.exe's own
    /// process handle table is torn down without OnFormClosing ever running (a crash, a forced
    /// kill, a system shutdown). Null is accepted so this class stays usable without one, e.g. in
    /// a unit test that only cares about the graceful Stop() path.</param>
    public ManagedProcess(string name, ChildProcessJob? job = null)
    {
        _name = name;
        _job = job;
    }

    public async Task<StartupFailure?> StartAndWaitHealthyAsync(
        ProcessStartInfo startInfo, Uri healthUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Confirmed live, not theoretical: without this check, a stale/unrelated process already
        // listening on this port (e.g. a leftover instance from a previous run) answers the health
        // poll below successfully, the health check declares success, and the child this method
        // just spawned - which failed to bind the same port and already exited - goes unnoticed.
        // Failing fast here means an occupied port is reported clearly, never silently masked by
        // whatever else is already listening.
        if (IsPortAlreadyInUse(healthUrl.Port))
            return new StartupFailure(_name, $"Port {healthUrl.Port} is already in use by another process. Close it and restart Kairon.");

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

        _job?.AssignProcess(_process);

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

    /// <summary>A real bind attempt, not a "connect and see" probe - the latter would itself be
    /// fooled by exactly the process this check exists to catch. Immediately releases the port
    /// either way; this is a point-in-time check, not a reservation.</summary>
    private static bool IsPortAlreadyInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
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
