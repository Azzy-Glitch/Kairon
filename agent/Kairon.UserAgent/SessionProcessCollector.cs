using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kairon.UserAgent;

/// <summary>
/// Per-interactive-session process collector: KAIRON.UserAgent's counterpart to
/// agent/Kairon.Agent/MachineRegistrationService.cs's machine-level heartbeat. Runs as the
/// logged-in user (auto-started by a logon-triggered Scheduled Task - see installer/Kairon.iss),
/// so it can read CPU/memory/start-time for that user's own processes directly - no ACL grants, no
/// elevated service account (docs/DESKTOP_SHELL.md).
///
/// MachineId is derived identically to Kairon.Agent's own copy (same hostname -> same SHA-256 ->
/// same GUID), so both components' telemetry lands on the same Machine with zero pairing step.
/// Authenticates with the same AgentKey the Windows Service already registered the machine with.
/// </summary>
public sealed class SessionProcessCollector : BackgroundService
{
    private readonly HttpClient _http;
    private readonly UserAgentOptions _options;
    private readonly ILogger<SessionProcessCollector> _logger;
    private readonly Guid _machineId;
    private readonly int _sessionId;
    private readonly string _userName;
    private readonly CpuBaselineCache _cpuCache = new();

    public SessionProcessCollector(HttpClient http, IOptions<UserAgentOptions> options,
        ILogger<SessionProcessCollector> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _machineId = DeriveMachineId(System.Environment.MachineName);
        _sessionId = Process.GetCurrentProcess().SessionId;
        _userName = SafeUserName();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "kairon-useragent: collecting processes for session {SessionId} ({UserName}), machine {MachineId}",
            _sessionId, _userName, _machineId);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(3, _options.HeartbeatIntervalSeconds)));
        do
        {
            try
            {
                await PollAndSendAsync(stoppingToken);
            }
            // Keyed on the token actually being cancelled, not the exception's type - see
            // agent/Kairon.Agent/MachineRegistrationService.cs's identical fix. An aborted HTTP
            // connection (e.g. the backend restarting mid-request) surfaces as a
            // TaskCanceledException/OperationCanceledException even though nobody asked this
            // service to stop; filtering on "is not OperationCanceledException" let that escape
            // uncaught and silently exit the whole process (confirmed live during lifecycle
            // acceptance testing - this exact bug was fixed in Kairon.Agent earlier but not
            // ported to this sibling component when it was written).
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "kairon-useragent: heartbeat cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAndSendAsync(CancellationToken cancellationToken)
    {
        using var source = new SystemProcessSnapshotSource(_sessionId);
        var samples = CollectSamples(source, _logger);
        var parentIds = ToolhelpProcessSnapshot.GetParentProcessIds();
        var now = DateTime.UtcNow;

        var cpuPercents = _cpuCache.Update(
            samples.Select(s => (s.ProcessId, s.StartedAt, s.TotalProcessorTime, now)),
            System.Environment.ProcessorCount);

        // Only processes with a real, measured second sample are ever reported - never a
        // fabricated first-tick value (a process seen for the first time this poll simply waits
        // one more interval before appearing).
        var processes = samples
            .Where(s => cpuPercents.ContainsKey((s.ProcessId, s.StartedAt)))
            .OrderByDescending(s => cpuPercents[(s.ProcessId, s.StartedAt)])
            .Take(Math.Max(1, _options.MaxProcesses))
            .Select(s => new
            {
                processId = s.ProcessId,
                startedAt = s.StartedAt,
                name = s.Name,
                executable = s.Executable,
                runtime = "Unknown",
                cpuPercent = cpuPercents[(s.ProcessId, s.StartedAt)],
                memoryBytes = s.MemoryBytes,
                parentProcessId = parentIds.TryGetValue(s.ProcessId, out var ppid) ? (int?)ppid : null
            })
            .ToList();

        var heartbeat = new
        {
            timestamp = now,
            sessionId = _sessionId,
            userName = _userName,
            processes
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/agent/machines/{_machineId}/user-session/heartbeat")
        {
            Content = JsonContent.Create(heartbeat)
        };
        request.Headers.Add("X-Kairon-Agent-Key", _options.AgentKey);

        await _http.SendAsync(request, timeout.Token);
    }

    /// <summary>
    /// Isolates a single process's read failure (access denied, exited mid-read) from the rest of
    /// the batch and from the heartbeat itself - the same fail-open philosophy as
    /// ProcessWatcher.PollAsync and MachineRegistrationService.HeartbeatAsync. Static and taking an
    /// IProcessSnapshotSource so this orchestration is unit-testable without real OS processes.
    /// </summary>
    public static List<RawProcessSample> CollectSamples(IProcessSnapshotSource source, ILogger? logger)
    {
        var results = new List<RawProcessSample>();
        foreach (var pid in source.GetCandidateProcessIds())
        {
            try
            {
                results.Add(source.ReadSample(pid));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "kairon-useragent: could not read metadata for process {ProcessId}", pid);
            }
        }

        return results;
    }

    private static string SafeUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return System.Environment.UserName;
        }
    }

    /// <summary>Must stay byte-for-byte identical to
    /// agent/Kairon.Agent/MachineRegistrationService.cs's copy - both components need to land on
    /// the same Machine with zero coordination. DeriveMachineIdParityTests.cs guards this.</summary>
    public static Guid DeriveMachineId(string hostName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hostName.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
