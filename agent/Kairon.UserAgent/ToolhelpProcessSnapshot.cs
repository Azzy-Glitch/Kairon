using System.Runtime.InteropServices;

namespace Kairon.UserAgent;

/// <summary>
/// Parent-process-id lookup via the Toolhelp snapshot API (kernel32's CreateToolhelp32Snapshot) -
/// one system-wide native call per poll, not a per-process query, and available to a standard,
/// unprivileged user token: this is the same mechanism Task Manager/Process Explorer use to show
/// parent PIDs without elevation, not a permission hack.
/// </summary>
public static class ToolhelpProcessSnapshot
{
    private const uint Th32csSnapprocess = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Maps process id to parent process id for every process visible in one snapshot. Returns an
    /// empty map (never throws) if the snapshot itself can't be taken - parent PID is enrichment,
    /// never something that should block process collection.
    /// </summary>
    public static IReadOnlyDictionary<int, int> GetParentProcessIds()
    {
        var result = new Dictionary<int, int>();
        var snapshot = IntPtr.Zero;
        try
        {
            snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;

            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return result;

            do
            {
                result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            } while (Process32Next(snapshot, ref entry));
        }
        catch
        {
            // Never let a native-interop failure here take down process collection.
        }
        finally
        {
            if (snapshot != IntPtr.Zero) CloseHandle(snapshot);
        }

        return result;
    }
}
