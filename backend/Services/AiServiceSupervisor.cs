using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kairon.Backend.Services;

/// <summary>
/// Windows-service mode: the backend owns the local AI service process. In desktop/dev mode the
/// desktop shell started both processes; a Windows service has no such parent, and the packaged
/// AI service (a PyInstaller uvicorn executable) cannot itself speak the SCM protocol. So when
/// AiService:ExecutablePath is configured the backend starts it as a child - inheriting the
/// backend's own low-privilege service identity - inside a kill-on-close job object (it can never
/// outlive the backend), hands it the per-start transport secret (AiService:ApiKey) through its
/// environment only, restarts it with bounded backoff if it exits, and stops it with the service.
/// </summary>
public sealed class AiServiceSupervisor : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly string _executable;
    private readonly string _transportKey;
    private readonly string _tempDirectory;
    private readonly ILogger<AiServiceSupervisor> _logger;
    private readonly KillOnCloseJob _job = new();
    private Process? _process;

    public AiServiceSupervisor(string executable, string transportKey, string tempDirectory, ILogger<AiServiceSupervisor> logger)
    {
        _executable = executable;
        _transportKey = transportKey;
        _tempDirectory = tempDirectory;
        _logger = logger;
    }

    public ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo(_executable)
        {
            // Not redirected: the packaged AI service is a PyInstaller one-file executable (a
            // bootloader plus the real server as its child). A redirected pipe inherited by that
            // grandchild keeps WaitForExitAsync waiting for EOF after the bootloader dies, so the
            // supervisor would never notice. As a service there is no console to inherit anyway.
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_executable)!
        };
        info.Environment["KAIRON_AI_API_KEY"] = _transportKey;
        // PyInstaller's onefile bootloader unpacks into TEMP; give it a private folder under the
        // service's own data root rather than relying on the virtual account's profile.
        info.Environment["TEMP"] = _tempDirectory;
        info.Environment["TMP"] = _tempDirectory;
        return info;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_tempDirectory);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var process = Process.Start(BuildStartInfo())
                    ?? throw new InvalidOperationException("The AI service process did not start.");
                _process = process;
                _job.Assign(process);
                _logger.LogInformation("Started the local AI service (pid {Pid}).", process.Id);
                await process.WaitForExitAsync(stoppingToken);
                _logger.LogWarning("The local AI service exited with code {Code}; restarting.", process.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Could not start the local AI service: {Error}", ex.GetType().Name);
            }
            finally
            {
                _process = null;
                // Whatever is still in the job (e.g. the server process of a bootloader that died)
                // would keep holding port 8001 and make every restart fail; clear it first.
                _job.TerminateAll();
            }

            failures = DateTime.UtcNow - started > TimeSpan.FromMinutes(5) ? 0 : failures + 1;
            try { await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        await base.StopAsync(cancellationToken);
        _job.Dispose();
    }

    /// <summary>Kill-on-close Windows job object: if the backend dies for any reason, the kernel
    /// terminates the AI child with it (no orphan holding port 8001 or the transport secret).</summary>
    private sealed class KillOnCloseJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private IntPtr _handle;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        public KillOnCloseJob()
        {
            if (!OperatingSystem.IsWindows()) return;
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return;
            var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
            {
                CloseHandle(handle);
                return;
            }
            _handle = handle;
        }

        public void Assign(Process process)
        {
            if (_handle != IntPtr.Zero) AssignProcessToJobObject(_handle, process.Handle);
        }

        /// <summary>Terminates every process still in the job; the job stays usable.</summary>
        public void TerminateAll()
        {
            if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
