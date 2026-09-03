using System.Runtime.InteropServices;

namespace Kairon.Desktop;

/// <summary>
/// A Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE - the OS-level guarantee that the
/// backend and AI child processes die the moment Kairon.exe's process object is torn down, for any
/// reason: a normal window close, a crash, or the process being killed from Task Manager or a system
/// shutdown that never runs a single line of our own shutdown code.
///
/// This exists because Process.Start() children are NOT tied to their parent's lifetime on Windows -
/// closing the launching process leaves them running independently unless something explicit binds
/// them. The graceful path (MainForm.OnFormClosing calling ManagedProcess.Stop()) only covers the
/// case where the shutdown handler actually gets to run; this job object is the fallback that covers
/// every other case, including the "Kairon.exe itself doesn't exit" scenario, since Windows releases
/// this handle - and with it, the child processes - as soon as the process handle table is torn down,
/// independent of whether our own code ran.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly IntPtr _handle;

    /// <summary>Null when job object creation or configuration failed (e.g. a locked-down
    /// environment) - AssignProcess then no-ops rather than throwing, so a job-object failure only
    /// gives up the OS-level safety net, it never blocks the product from starting.</summary>
    public ChildProcessJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, lpName: null);
        if (handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(handle);
            _handle = IntPtr.Zero;
            return;
        }

        _handle = handle;
    }

    /// <summary>Best-effort: a process that exited between Process.Start() and this call, or one
    /// already in another job on an OS build that disallows nested jobs, simply isn't covered by
    /// the OS-level guarantee - the graceful ManagedProcess.Stop() path still applies to it.</summary>
    public void AssignProcess(System.Diagnostics.Process process)
    {
        if (_handle == IntPtr.Zero) return;

        try
        {
            AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            // Process may have exited already, or Handle access failed - non-fatal either way.
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }
}
