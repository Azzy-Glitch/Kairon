using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kairon.UserAgent;

/// <summary>One approved restart, as handed out by the backend. It identifies a process; it is not
/// a command to run.</summary>
public sealed record ProcessRestartInstruction(Guid CommandId, int ProcessId, DateTime ProcessStartedAt,
    string Executable, string WorkingDirectory);

public sealed record ProcessRestartOutcome(bool Succeeded, int? NewProcessId, string? Error)
{
    public static ProcessRestartOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// Restarts an application process in this UserAgent's own session, exactly as Windows reports it:
/// the same executable, the same command line, in the folder the SDK reported. It verifies the
/// process first (same session, same start time, same executable as the approval), never touches
/// Windows components or KAIRON itself, and runs nothing it was told to run - the command line is
/// read from the operating system for the very process being replaced.
/// </summary>
public static class ProcessRestarter
{
    /// <summary>Launchers that hand the real interpreter a process of its own. Restarting only that
    /// child would relaunch it without what the launcher set up (a virtual environment, for one).</summary>
    private static readonly HashSet<string> PythonLaunchers = new(StringComparer.OrdinalIgnoreCase) { "py.exe", "pyw.exe" };

    public static ProcessRestartOutcome Restart(ProcessRestartInstruction instruction, int ownSessionId)
    {
        if (!OperatingSystem.IsWindows()) return ProcessRestartOutcome.Fail("Process restart requires Windows.");
        if (!Directory.Exists(instruction.WorkingDirectory))
            return ProcessRestartOutcome.Fail("The application's working directory no longer exists.");

        var parents = ToolhelpProcessSnapshot.GetParentProcessIds();
        var sdkProcess = Describe(instruction.ProcessId);
        if (sdkProcess is null) return ProcessRestartOutcome.Fail("The application process is no longer running.");
        if (Verify(sdkProcess, instruction, ownSessionId) is { } mismatch) return ProcessRestartOutcome.Fail(mismatch);

        var root = ChooseRestartRoot(sdkProcess, parents, Describe);
        if (Refusal(root.Executable) is { } refusal) return ProcessRestartOutcome.Fail(refusal);
        var commandLine = ReadCommandLine(root.ProcessId);
        if (string.IsNullOrWhiteSpace(commandLine)) return ProcessRestartOutcome.Fail("Windows did not report the application's command line.");

        try
        {
            using var running = Process.GetProcessById(root.ProcessId);
            running.Kill(entireProcessTree: true);
            if (!running.WaitForExit(10_000)) return ProcessRestartOutcome.Fail("The application did not exit within 10 seconds.");
        }
        catch (ArgumentException) { /* exited on its own meanwhile - still relaunch it */ }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ProcessRestartOutcome.Fail($"Windows refused to stop the application ({ex.GetType().Name}).");
        }

        try
        {
            return new ProcessRestartOutcome(true, Launch(root.Executable, commandLine, instruction.WorkingDirectory), null);
        }
        catch (Win32Exception ex)
        {
            return ProcessRestartOutcome.Fail($"The application was stopped but could not be started again (Windows error {ex.NativeErrorCode}).");
        }
    }

    /// <summary>A process as the operating system describes it.</summary>
    public sealed record ProcessFacts(int ProcessId, int SessionId, DateTime StartedAt, string Executable);

    public static string? Verify(ProcessFacts process, ProcessRestartInstruction instruction, int ownSessionId)
    {
        if (process.SessionId != ownSessionId)
            return "The application runs in a different Windows session; only that user's UserAgent may restart it.";
        if (Math.Abs((process.StartedAt - instruction.ProcessStartedAt).TotalSeconds) > 1)
            return "The process id now belongs to a different process than the one approved; nothing was changed.";
        if (!string.Equals(process.Executable, instruction.Executable, StringComparison.OrdinalIgnoreCase))
            return "The process runs a different program than the one approved; nothing was changed.";
        return Refusal(process.Executable);
    }

    /// <summary>
    /// Walks from the process that talks to KAIRON up to the command the developer actually started,
    /// but only through launchers of that same application: a parent with the same file name (a
    /// virtual environment's python.exe starting the real interpreter), the py launcher, or a script
    /// shim in a Scripts folder (uvicorn.exe, flask.exe, ...) starting python. It never climbs into a
    /// shell, terminal, IDE or anything else, and never to a process that started after its child
    /// (a recycled process id).
    /// </summary>
    public static ProcessFacts ChooseRestartRoot(ProcessFacts start, IReadOnlyDictionary<int, int> parents,
        Func<int, ProcessFacts?> describe)
    {
        var current = start;
        for (var depth = 0; depth < 4; depth++)
        {
            if (!parents.TryGetValue(current.ProcessId, out var parentId) || parentId <= 0) break;
            var parent = describe(parentId);
            if (parent is null || parent.SessionId != current.SessionId || parent.StartedAt > current.StartedAt ||
                string.IsNullOrEmpty(parent.Executable) || Refusal(parent.Executable) is not null) break;
            var child = Path.GetFileName(current.Executable);
            var parentName = Path.GetFileName(parent.Executable);
            var launcher = string.Equals(parentName, child, StringComparison.OrdinalIgnoreCase) ||
                           PythonLaunchers.Contains(parentName) ||
                           (child.StartsWith("python", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetFileName(Path.GetDirectoryName(parent.Executable)), "Scripts", StringComparison.OrdinalIgnoreCase));
            if (!launcher) break;
            current = parent;
        }
        return current;
    }

    /// <summary>Windows components and KAIRON's own processes are never restarted.</summary>
    public static string? Refusal(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            return "The application's executable path is unknown.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) &&
            executable.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return "Windows components are never restarted.";
        var kairon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kairon") + "\\";
        if (executable.StartsWith(kairon, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).StartsWith("Kairon", StringComparison.OrdinalIgnoreCase))
            return "KAIRON's own processes are never restarted.";
        return null;
    }

    private static ProcessFacts? Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new ProcessFacts(process.Id, process.SessionId, process.StartTime.ToUniversalTime(), ImagePath(process.Id) ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    // --- Windows ---

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const uint CreateNewConsole = 0x00000010;

    private static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private static string? ReadCommandLine(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid) return null;
        _ = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 64 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0) return null;
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Starts the exact executable with the exact command line Windows reported, in its
    /// folder, as this user, in a new console so a console application has a window again.</summary>
    private static int Launch(string executable, string commandLine, string workingDirectory)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
        if (!CreateProcess(executable, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNewConsole,
                IntPtr.Zero, workingDirectory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        return info.dwProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
