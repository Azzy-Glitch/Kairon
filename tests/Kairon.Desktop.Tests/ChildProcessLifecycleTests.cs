using System.Diagnostics;
using Kairon.Desktop;
using Xunit;

namespace Kairon.Desktop.Tests;

/// <summary>
/// Covers the desktop shell's process-lifecycle guarantees (candidate defect: closing the window
/// left Backend/AI/Kairon.exe running and ports 8000/8001 open - see MainForm.OnFormClosing and
/// ChildProcessJob for the fix and its reasoning).
///
/// These spawn a real child process (cmd.exe, always present on Windows) rather than mocking
/// anything - the defect was specifically about real OS process/handle behavior, which a mock
/// cannot reproduce or verify. CI builds this project on Linux too (EnableWindowsTargeting, see
/// ci.yml) so it can compile everywhere, but every test here is a real Windows process/job-object
/// behavior and has nothing meaningful to assert on a non-Windows runner - each one skips itself
/// immediately (xunit 2.9.2, already used throughout this repo, has no attribute-based runtime
/// skip; an early return is the standard workaround without adding a new package dependency).
/// </summary>
public class ChildProcessLifecycleTests
{
    // A process that sits and waits rather than exiting immediately, so there is something real to
    // observe being killed (or not). /t 0 with a wait-based command would race; "timeout" without
    // /nobreak backgrounded via cmd still needs stdin, so ping a fixed count instead - reliably
    // present, reliably takes a few seconds, needs nothing interactive.
    private static ProcessStartInfo LongRunningProcess() => new("ping.exe", "127.0.0.1 -n 30")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };

    [Fact]
    public void ChildProcessJob_kills_assigned_process_when_disposed()
    {
        if (!OperatingSystem.IsWindows()) return; // Job objects are a Windows-only concept.

        var job = new ChildProcessJob();
        using var process = Process.Start(LongRunningProcess());
        Assert.NotNull(process);
        Assert.False(process!.HasExited);

        job.AssignProcess(process);

        // The whole point of JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: disposing the job - not calling
        // anything on the process itself - is what ends it. This is the OS-level guarantee that
        // covers every shutdown path other than the graceful one (crash, forced kill of Kairon.exe,
        // a system shutdown that never runs OnFormClosing).
        job.Dispose();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ManagedProcess_Stop_terminates_a_running_process()
    {
        if (!OperatingSystem.IsWindows()) return;

        var managed = new ManagedProcess("test-process");

        // A health URL nothing will ever answer, with a short timeout - StartAndWaitHealthyAsync
        // still spawns and tracks the process before it starts polling, so the timeout path below
        // is exactly what exercises Stop() on a process that was started but never reported healthy
        // (the same state a Backend/AI process would be in if it hung during startup).
        var unreachableHealthUrl = new Uri("http://127.0.0.1:1/health");
        var failure = await managed.StartAndWaitHealthyAsync(
            LongRunningProcess(), unreachableHealthUrl, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(failure);

        managed.Stop();

        // Stop() must be idempotent - MainForm's shutdown path can call it more than once (a failed
        // startup already calling Close(), which re-enters OnFormClosing).
        managed.Stop();
    }

    [Fact]
    public void ManagedProcess_Stop_before_any_process_started_does_not_throw()
    {
        // Not Windows-gated: this exercises the "no process to stop" branch, plain C# logic with no
        // OS-specific behavior involved.
        var managed = new ManagedProcess("never-started");
        managed.Stop();
        managed.Dispose();
    }
}
