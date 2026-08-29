using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kairon.Agent.ProcessWatch;

/// <summary>
/// Watches a named process for start/stop/crash and high CPU, entirely via
/// System.Diagnostics.Process - the well-bounded half of the Agent's zero-code monitoring (the
/// log tailer is the higher-variance half; see LogTailing/LogTailer.cs).
///
/// Reports three event types: ProcessStarted (first time the process is seen), ProcessCrash (it
/// was running and is no longer found), ProcessHighResource (CPU over threshold). A CPU
/// percentage needs two samples to compute (processor time delta over wall-clock delta), so the
/// first poll after a process is (re)detected only records the baseline and reports nothing.
/// </summary>
public class ProcessWatcher : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly AgentEventClient _client;
    private readonly ILogger<ProcessWatcher> _logger;

    private bool _wasRunning;
    private int? _lastKnownPid;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt;

    public ProcessWatcher(IOptions<AgentOptions> options, AgentEventClient client, ILogger<ProcessWatcher> logger)
    {
        _options = options.Value;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableProcessWatch || string.IsNullOrWhiteSpace(_options.TargetProcessName))
        {
            _logger.LogInformation("kairon-agent: process watch disabled (no TargetProcessName configured)");
            return;
        }

        _logger.LogInformation("kairon-agent: watching process {ProcessName}", _options.TargetProcessName);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.ProcessPollIntervalSeconds)));

        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            // Keyed on the token actually being cancelled, not the exception's type - see the
            // identical comment in MachineRegistrationService.ExecuteAsync. A process that exits
            // mid-read, access-denied on a system process, or a transient network cancellation
            // must never stop the Agent's own loop - the next tick tries again.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "kairon-agent: process poll failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName(_options.TargetProcessName);

        try
        {
            if (processes.Length == 0)
            {
                if (_wasRunning)
                {
                    await _client.SendAsync(new AgentEventPayload
                    {
                        EventType = "ProcessCrash",
                        Severity = "Critical",
                        Message = $"Process '{_options.TargetProcessName}' is no longer running (was PID {_lastKnownPid}).",
                        // Non-null here: ExecuteAsync already returned early when TargetProcessName was unset.
                        Source = _options.TargetProcessName!
                    }, cancellationToken);

                    _wasRunning = false;
                    _lastKnownPid = null;
                }

                return;
            }

            // The demo scenario runs one instance; watching the first is enough for this pass.
            var process = processes[0];

            if (!_wasRunning)
            {
                await _client.SendAsync(new AgentEventPayload
                {
                    EventType = "ProcessStarted",
                    Severity = "Info",
                    Message = $"Process '{_options.TargetProcessName}' detected running (PID {process.Id}).",
                    Source = _options.TargetProcessName!
                }, cancellationToken);

                _wasRunning = true;
                _lastKnownPid = process.Id;
                _lastCpuTime = process.TotalProcessorTime;
                _lastSampleAt = DateTime.UtcNow;
                return;
            }

            process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            var elapsedWallMs = (now - _lastSampleAt).TotalMilliseconds;
            var elapsedCpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;

            var cpuPercent = elapsedWallMs > 0
                ? Math.Round(elapsedCpuMs / (System.Environment.ProcessorCount * elapsedWallMs) * 100, 1)
                : 0;

            _lastCpuTime = cpuTime;
            _lastSampleAt = now;
            _lastKnownPid = process.Id;

            if (cpuPercent >= _options.HighCpuPercentThreshold)
            {
                var metadata = JsonSerializer.Serialize(new
                {
                    cpuPercent,
                    memoryMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
                    threadCount = process.Threads.Count
                });

                await _client.SendAsync(new AgentEventPayload
                {
                    EventType = "ProcessHighResource",
                    Severity = "Warning",
                    Message = $"Process '{_options.TargetProcessName}' CPU at {cpuPercent}% (threshold {_options.HighCpuPercentThreshold}%).",
                    Source = _options.TargetProcessName!,
                    MetadataJson = metadata
                }, cancellationToken);
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
