using System.Diagnostics;
using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

/// <summary>
/// The real thing on Windows: a throwaway python process is restarted by ProcessRestarter - command
/// line read from the OS, process stopped, the identical command started again in the same folder.
/// Skipped (passes vacuously) where Windows or a Python interpreter outside the Windows folder is
/// unavailable, e.g. the Linux CI runner.
/// </summary>
public sealed class ProcessRestarterWindowsTests
{
    [Fact]
    public void ARealProcessIsRestartedWithTheSameCommandInTheSameFolder()
    {
        var python = FindPython();
        if (!OperatingSystem.IsWindows() || python is null) return;

        var folder = Directory.CreateTempSubdirectory("kairon-restart-").FullName;
        var marker = "kairon-restart-test-" + Guid.NewGuid().ToString("N");
        var started = Process.Start(new ProcessStartInfo(python, $"-c \"import time; time.sleep(120)\" {marker}")
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true
        })!;
        Process? restarted = null;
        try
        {
            Thread.Sleep(500);
            started.Refresh();
            var instruction = new ProcessRestartInstruction(Guid.NewGuid(), started.Id, started.StartTime.ToUniversalTime(),
                started.MainModule!.FileName, folder);

            var outcome = ProcessRestarter.Restart(instruction, Process.GetCurrentProcess().SessionId);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.True(started.WaitForExit(5000));
            restarted = Process.GetProcessById(outcome.NewProcessId!.Value);
            Assert.False(restarted.HasExited);
            Assert.NotEqual(started.Id, restarted.Id);
            Assert.Equal(instruction.Executable, restarted.MainModule!.FileName, ignoreCase: true);
        }
        finally
        {
            try { if (!started.HasExited) started.Kill(entireProcessTree: true); } catch { }
            try { restarted?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AProcessThatIsNotTheApprovedOneIsLeftAlone()
    {
        var python = FindPython();
        if (!OperatingSystem.IsWindows() || python is null) return;

        var started = Process.Start(new ProcessStartInfo(python, "-c \"import time; time.sleep(60)\"")
        {
            UseShellExecute = false, CreateNoWindow = true
        })!;
        try
        {
            Thread.Sleep(300);
            // Approved for an earlier process that happened to have the same id.
            var stale = new ProcessRestartInstruction(Guid.NewGuid(), started.Id, DateTime.UtcNow.AddHours(-1),
                started.MainModule!.FileName, Path.GetTempPath());

            var outcome = ProcessRestarter.Restart(stale, Process.GetCurrentProcess().SessionId);

            Assert.False(outcome.Succeeded);
            Assert.False(started.HasExited);
        }
        finally { try { started.Kill(entireProcessTree: true); } catch { } }
    }

    private static string? FindPython()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "python.exe");
                if (File.Exists(candidate) && !candidate.StartsWith(windows, StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
