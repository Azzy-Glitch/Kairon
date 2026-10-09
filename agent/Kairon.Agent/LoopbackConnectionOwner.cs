using System.Net;
using System.Runtime.InteropServices;

namespace Kairon.Agent;

/// <summary>
/// Identifies, from the operating system's own TCP table, which process owns the client end of an
/// accepted loopback connection. This is what lets KAIRON know which process sent a machine proof
/// without trusting anything that process says about itself. Listing owning process ids needs no
/// administrator rights. Returns null when it cannot be determined (non-Windows, or the
/// connection already closed); callers treat that as "process unknown", never as a guess.
/// </summary>
public static class LoopbackConnectionOwner
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;

    public static int? Find(IPEndPoint? client, int serverPort)
    {
        if (!OperatingSystem.IsWindows() || client is null || !IPAddress.IsLoopback(client.Address)) return null;
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew; retry with the new size
                if (result != 0) return null;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    if (Port(row.LocalPort) == client.Port && Port(row.RemotePort) == serverPort &&
                        new IPAddress(row.LocalAddr).Equals(client.Address) && row.OwningPid > 0)
                        return (int)row.OwningPid;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    // Ports are stored in network byte order in the low 16 bits.
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);
}
