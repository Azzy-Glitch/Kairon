using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kairon.Agent;

/// <summary>
/// Registers this machine once, then heartbeats the Agent's watched process on an interval -
/// "Basic Monitoring" (docs/DESKTOP_SHELL.md): zero-code discovery, no SDK involved. Distinct
/// from ProcessWatcher, which reports EVENTS (start/crash/high-CPU) for detection/correlation;
/// this reports current INVENTORY state for the read-only Machines/Applications view.
///
/// MachineId is derived deterministically from the hostname (SHA-256, first 16 bytes as a GUID)
/// rather than stored in a local file - simpler, and re-registering the same machine always
/// resolves to the same identity without adding another piece of local state to manage.
/// </summary>
public class MachineRegistrationService : BackgroundService
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly ILogger<MachineRegistrationService> _logger;
    private readonly Guid _machineId;

    public MachineRegistrationService(HttpClient http, IOptions<AgentOptions> options,
        ILogger<MachineRegistrationService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _machineId = DeriveMachineId(System.Environment.MachineName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableMachineRegistration)
        {
            _logger.LogInformation("kairon-agent: machine registration disabled");
            return;
        }

        if (!await RegisterAsync(stoppingToken))
        {
            _logger.LogDebug("kairon-agent: initial machine registration did not succeed; heartbeats will keep retrying");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatIntervalSeconds)));
        do
        {
            try
            {
                await HeartbeatAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "kairon-agent: heartbeat failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> RegisterAsync(CancellationToken cancellationToken)
    {
        var registration = new
        {
            machineId = _machineId,
            hostName = System.Environment.MachineName,
            operatingSystem = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            agentVersion = "1.0.0",
            agentKey = _options.AgentKey
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            var response = await _http.PostAsJsonAsync("api/agent/register", registration, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "kairon-agent: machine registration failed");
            return false;
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var processes = new List<object>();

        if (_options.EnableProcessWatch && !string.IsNullOrWhiteSpace(_options.TargetProcessName))
        {
            var matches = Process.GetProcessesByName(_options.TargetProcessName);
            try
            {
                if (matches.Length > 0)
                {
                    var process = matches[0];
                    processes.Add(new
                    {
                        processId = process.Id,
                        startedAt = process.StartTime.ToUniversalTime(),
                        name = _options.TargetProcessName,
                        executable = SafeFileName(process),
                        runtime = ".NET",
                        cpuPercent = 0.0,
                        memoryBytes = process.WorkingSet64
                    });
                }
            }
            finally
            {
                foreach (var process in matches) process.Dispose();
            }
        }

        var heartbeat = new { timestamp = DateTime.UtcNow, processes };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/agent/machines/{_machineId}/heartbeat")
        {
            Content = JsonContent.Create(heartbeat)
        };
        request.Headers.Add("X-Kairon-Agent-Key", _options.AgentKey);

        await _http.SendAsync(request, timeout.Token);
    }

    private static string SafeFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access-denied on a process owned by another user/elevated context - not fatal,
            // just report no path.
            return string.Empty;
        }
    }

    private static Guid DeriveMachineId(string hostName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hostName.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
