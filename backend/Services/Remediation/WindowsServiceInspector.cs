using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Services.Remediation.Tools;

namespace Kairon.Backend.Services.Remediation;

/// <summary>Why a Windows service can (or cannot) be a remediation target. Every value except
/// <see cref="Eligible"/> is a refusal; none of them is ever relaxed by configuration.</summary>
public enum ServiceEligibility
{
    Eligible,
    /// <summary>A KAIRON component (Kairon.Agent, Kairon.Backend, ...). Stopping one would blind
    /// or disable the very witness/executor that authorizes remediation.</summary>
    KaironInfrastructure,
    /// <summary>Not a stand-alone Win32 service process: drivers, shared svchost services and
    /// per-user service templates are Windows components, not application services.</summary>
    NotApplicationService,
    /// <summary>Launch-protected (PPL) service - Windows itself protects it.</summary>
    Protected,
    /// <summary>Executable lives under %SystemRoot%: a Windows component, not an application.</summary>
    WindowsComponent,
    /// <summary>The service configuration could not be read, so its identity is unknown.</summary>
    Unknown
}

/// <summary>Read-only facts about one local Windows service, gathered without changing it.</summary>
public sealed record WindowsServiceProbe
{
    public required string ServiceName { get; init; }
    public bool Exists { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>SCM state: 1 Stopped, 2 StartPending, 3 StopPending, 4 Running, 5-7 transitional/paused.</summary>
    public int? State { get; init; }
    public string? ImagePath { get; init; }
    public string? StartAccount { get; init; }
    public int ServiceType { get; init; }
    public ServiceEligibility Eligibility { get; init; } = ServiceEligibility.Unknown;
    public bool CanQueryStatus { get; init; }
    public bool CanStart { get; init; }
    public bool CanStop { get; init; }
    /// <summary>SHA-256 over the service's executable identity (type, image path, run-as account).
    /// A same-named service recreated around a different executable or account gets a different
    /// value, so a stored authorization cannot silently transfer to it.</summary>
    public string? IdentityHash { get; init; }
    /// <summary>Win32 error from the last failed SCM call, for the advanced-details view only.</summary>
    public int? Win32Error { get; init; }

    public bool HasRightsFor(IEnumerable<string> operations) => operations.All(HasRightsFor);

    public bool HasRightsFor(string operation) => CanQueryStatus && operation switch
    {
        ServiceToolNames.RunHealthCheck => true,
        ServiceToolNames.StartService => CanStart,
        ServiceToolNames.StopService => CanStop,
        ServiceToolNames.RestartService => CanStart && CanStop,
        _ => false
    };

    /// <summary>The SCM rights each operation needs, in the order an operator grants them.</summary>
    public static IReadOnlyList<string> RequiredRights(IEnumerable<string> operations)
    {
        var ops = operations.ToHashSet(StringComparer.Ordinal);
        var rights = new List<string> { "Query" };
        if (ops.Contains(ServiceToolNames.StartService) || ops.Contains(ServiceToolNames.RestartService)) rights.Add("Start");
        if (ops.Contains(ServiceToolNames.StopService) || ops.Contains(ServiceToolNames.RestartService)) rights.Add("Stop");
        return rights;
    }
}

public sealed record WindowsServiceSummary(string ServiceName, string DisplayName, int State, ServiceEligibility Eligibility);

/// <summary>Read-only access to the LOCAL Service Control Manager, under the backend's own Windows
/// identity - the same identity sc.exe uses for remediation. Never changes a service, its
/// configuration or its security descriptor; it holds no right to do so.</summary>
public interface IWindowsServiceInspector
{
    bool IsSupported { get; }
    WindowsServiceProbe Probe(string serviceName);
    IReadOnlyList<WindowsServiceSummary> ListServices();
}

/// <summary>The machine the backend executes on. Remediation is local-only: sc.exe \\host against
/// another machine would authenticate as the backend's network identity with nothing binding that
/// remote SCM endpoint to the enrolled Agent, so it is refused rather than silently attempted.</summary>
public interface ILocalMachine
{
    bool IsLocal(string hostName);
}

public sealed class LocalMachine : ILocalMachine
{
    public static readonly LocalMachine Instance = new();
    public bool IsLocal(string hostName) =>
        !string.IsNullOrWhiteSpace(hostName) && hostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>KAIRON's service eligibility policy (docs/WINDOWS_PRODUCTION_REMEDIATION.md). Deliberately
/// structural rather than a long name list: KAIRON remediates stand-alone application services,
/// never KAIRON itself and never Windows components.</summary>
public static class ServiceEligibilityPolicy
{
    public const int Win32OwnProcess = 0x10;

    public static ServiceEligibility Evaluate(string serviceName, int serviceType, string? imagePath, bool launchProtected, string? systemRoot = null)
    {
        if (serviceName.StartsWith("Kairon", StringComparison.OrdinalIgnoreCase)) return ServiceEligibility.KaironInfrastructure;
        if (string.IsNullOrWhiteSpace(imagePath)) return ServiceEligibility.Unknown;
        // Exactly SERVICE_WIN32_OWN_PROCESS: excludes drivers, shared svchost services and the
        // per-user service bit (0x40) / interactive bit (0x100).
        if (serviceType != Win32OwnProcess) return ServiceEligibility.NotApplicationService;
        if (launchProtected) return ServiceEligibility.Protected;
        var executable = ExecutablePath(imagePath);
        var root = (systemRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\') + "\\";
        if (executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            executable.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
            executable.StartsWith(@"%SystemRoot%\", StringComparison.OrdinalIgnoreCase) ||
            executable.StartsWith(@"%windir%\", StringComparison.OrdinalIgnoreCase))
            return ServiceEligibility.WindowsComponent;
        return ServiceEligibility.Eligible;
    }

    public static string IdentityHash(int serviceType, string imagePath, string? startAccount) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{serviceType}\n{imagePath.Trim().ToUpperInvariant()}\n{(startAccount ?? "").Trim().ToUpperInvariant()}")));

    /// <summary>The executable portion of an SCM ImagePath (quoted, or up to the first ".exe").</summary>
    public static string ExecutablePath(string imagePath)
    {
        var path = imagePath.Trim();
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            return end > 0 ? path[1..end] : path.Trim('"');
        }
        var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? path[..(exe + 4)] : path;
    }

    public static string Describe(ServiceEligibility eligibility) => eligibility switch
    {
        ServiceEligibility.Eligible => "Eligible application service.",
        ServiceEligibility.KaironInfrastructure => "KAIRON's own services are never remediation targets.",
        ServiceEligibility.NotApplicationService => "Only stand-alone application services can be targeted; drivers and shared Windows services cannot.",
        ServiceEligibility.Protected => "Windows protects this service (launch-protected).",
        ServiceEligibility.WindowsComponent => "This is a Windows component (its executable is under the Windows directory).",
        _ => "The service configuration could not be read, so its identity cannot be verified."
    };
}

public sealed class WindowsServiceInspector : IWindowsServiceInspector
{
    public static readonly WindowsServiceInspector Instance = new();

    public bool IsSupported => OperatingSystem.IsWindows();

    public WindowsServiceProbe Probe(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return new WindowsServiceProbe { ServiceName = serviceName };
        return ProbeWindows(serviceName);
    }

    public IReadOnlyList<WindowsServiceSummary> ListServices() =>
        OperatingSystem.IsWindows() ? ListWindows() : [];

    [SupportedOSPlatform("windows")]
    private static WindowsServiceProbe ProbeWindows(string serviceName)
    {
        using var scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
        if (scm.IsInvalid) return new WindowsServiceProbe { ServiceName = serviceName, Win32Error = Marshal.GetLastWin32Error() };

        // Each right is requested on its own: OpenService performs Windows' real access check for
        // exactly that mask and grants nothing, so this observes - never exercises - the right.
        bool Can(uint access, out int error)
        {
            using var handle = Native.OpenService(scm, serviceName, access);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            return !handle.IsInvalid;
        }

        var canQuery = Can(Native.SERVICE_QUERY_STATUS, out var queryError);
        if (!canQuery && queryError == Native.ERROR_SERVICE_DOES_NOT_EXIST)
            return new WindowsServiceProbe { ServiceName = serviceName, Exists = false, Win32Error = queryError };
        var canStart = Can(Native.SERVICE_START, out _);
        var canStop = Can(Native.SERVICE_STOP, out _);

        int? state = null;
        if (canQuery)
        {
            using var status = Native.OpenService(scm, serviceName, Native.SERVICE_QUERY_STATUS);
            if (!status.IsInvalid && Native.QueryServiceStatus(status, out var s)) state = (int)s.dwCurrentState;
        }

        string? imagePath = null, account = null, displayName = null;
        var type = 0;
        var launchProtected = false;
        int? configError = null;
        using (var config = Native.OpenService(scm, serviceName, Native.SERVICE_QUERY_CONFIG))
        {
            if (config.IsInvalid) configError = Marshal.GetLastWin32Error();
            else
            {
                (type, imagePath, account, displayName) = Native.QueryConfig(config);
                launchProtected = Native.QueryLaunchProtected(config) != 0;
            }
        }

        var eligibility = imagePath is null ? ServiceEligibility.Unknown
            : ServiceEligibilityPolicy.Evaluate(serviceName, type, imagePath, launchProtected);
        if (serviceName.StartsWith("Kairon", StringComparison.OrdinalIgnoreCase)) eligibility = ServiceEligibility.KaironInfrastructure;
        return new WindowsServiceProbe
        {
            ServiceName = serviceName,
            Exists = true,
            DisplayName = displayName,
            State = state,
            ImagePath = imagePath,
            StartAccount = account,
            ServiceType = type,
            Eligibility = eligibility,
            CanQueryStatus = canQuery,
            CanStart = canStart,
            CanStop = canStop,
            IdentityHash = imagePath is null ? null : ServiceEligibilityPolicy.IdentityHash(type, imagePath, account),
            Win32Error = canQuery ? configError : queryError
        };
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowsServiceSummary> ListWindows()
    {
        using var scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT | Native.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the local Service Control Manager.");
        var result = new List<WindowsServiceSummary>();
        foreach (var (name, display, state) in Native.Enumerate(scm))
        {
            var eligibility = ServiceEligibility.Unknown;
            using (var config = Native.OpenService(scm, name, Native.SERVICE_QUERY_CONFIG))
            {
                if (!config.IsInvalid)
                {
                    var (type, image, _, _) = Native.QueryConfig(config);
                    eligibility = image is null ? ServiceEligibility.Unknown
                        : ServiceEligibilityPolicy.Evaluate(name, type, image, Native.QueryLaunchProtected(config) != 0);
                }
            }
            if (name.StartsWith("Kairon", StringComparison.OrdinalIgnoreCase)) eligibility = ServiceEligibility.KaironInfrastructure;
            result.Add(new WindowsServiceSummary(name, display, state, eligibility));
        }
        return result.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public const uint SC_MANAGER_CONNECT = 0x0001;
        public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        public const uint SERVICE_QUERY_CONFIG = 0x0001;
        public const uint SERVICE_QUERY_STATUS = 0x0004;
        public const uint SERVICE_START = 0x0010;
        public const uint SERVICE_STOP = 0x0020;
        public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int ERROR_MORE_DATA = 234;
        private const uint SERVICE_CONFIG_LAUNCH_PROTECTED = 12;
        private const uint SC_ENUM_PROCESS_INFO = 0;
        private const uint SERVICE_WIN32 = 0x30;
        private const uint SERVICE_STATE_ALL = 0x3;

        public sealed class ScHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
        {
            public ScHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryServiceConfigW
        {
            public uint dwServiceType, dwStartType, dwErrorControl;
            public IntPtr lpBinaryPathName, lpLoadOrderGroup;
            public uint dwTagId;
            public IntPtr lpDependencies, lpServiceStartName, lpDisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EnumServiceStatusProcessW
        {
            public IntPtr lpServiceName, lpDisplayName;
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ScHandle OpenSCManager(string? machineName, string? databaseName, uint access);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ScHandle OpenService(ScHandle manager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool QueryServiceStatus(ScHandle service, out ServiceStatus status);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfig(ScHandle service, IntPtr buffer, uint size, out uint needed);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfig2(ScHandle service, uint level, IntPtr buffer, uint size, out uint needed);

        [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusEx(ScHandle manager, uint level, uint type, uint state, IntPtr buffer,
            uint size, out uint needed, out uint returned, ref uint resume, string? group);

        public static (int Type, string? ImagePath, string? Account, string? DisplayName) QueryConfig(ScHandle service)
        {
            QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
            if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || needed == 0 || needed > 64 * 1024) return (0, null, null, null);
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfig(service, buffer, needed, out _)) return (0, null, null, null);
                var config = Marshal.PtrToStructure<QueryServiceConfigW>(buffer);
                return ((int)config.dwServiceType, Marshal.PtrToStringUni(config.lpBinaryPathName),
                    Marshal.PtrToStringUni(config.lpServiceStartName), Marshal.PtrToStringUni(config.lpDisplayName));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>SERVICE_LAUNCH_PROTECTED_INFO.dwLaunchProtected; 0 when unprotected or unreadable.</summary>
        public static uint QueryLaunchProtected(ScHandle service)
        {
            var buffer = Marshal.AllocHGlobal(sizeof(uint));
            try { return QueryServiceConfig2(service, SERVICE_CONFIG_LAUNCH_PROTECTED, buffer, sizeof(uint), out _) ? (uint)Marshal.ReadInt32(buffer) : 0; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static IEnumerable<(string Name, string Display, int State)> Enumerate(ScHandle manager)
        {
            uint resume = 0;
            var items = new List<(string, string, int)>();
            while (true)
            {
                EnumServicesStatusEx(manager, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, IntPtr.Zero, 0,
                    out var needed, out _, ref resume, null);
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_MORE_DATA || needed == 0) break;
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    var ok = EnumServicesStatusEx(manager, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, buffer, needed,
                        out _, out var returned, ref resume, null);
                    var more = !ok && Marshal.GetLastWin32Error() == ERROR_MORE_DATA;
                    if (!ok && !more) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate local services.");
                    var size = Marshal.SizeOf<EnumServiceStatusProcessW>();
                    for (var i = 0; i < returned; i++)
                    {
                        var entry = Marshal.PtrToStructure<EnumServiceStatusProcessW>(buffer + i * size);
                        items.Add((Marshal.PtrToStringUni(entry.lpServiceName) ?? "", Marshal.PtrToStringUni(entry.lpDisplayName) ?? "",
                            (int)entry.dwCurrentState));
                    }
                    if (!more) break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return items;
        }
    }
}
