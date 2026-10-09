using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Kairon.UserAgent;

/// <summary>
/// Collects the operator-approved application restarts addressed to this machine and this Windows
/// session, performs each with <see cref="ProcessRestarter"/>, and reports the outcome. Outbound
/// HTTP only - the UserAgent still opens no listener. Authenticates with its scoped UserAgent key.
/// </summary>
public sealed class ProcessRestartWorker : BackgroundService
{
    private readonly HttpClient _http;
    private readonly UserAgentOptions _options;
    private readonly ILogger<ProcessRestartWorker> _logger;
    private readonly Guid _machineId = SessionProcessCollector.DeriveMachineId(Environment.MachineName);
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private string? _key;

    public ProcessRestartWorker(HttpClient http, IOptions<UserAgentOptions> options, ILogger<ProcessRestartWorker> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableProcessRestart || !OperatingSystem.IsWindows()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.ProcessRestartPollSeconds, 1, 30)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("kairon-useragent: restart poll failed ({Type})", ex.GetType().Name);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (!AgentEndpointSecurity.IsAllowed(_http.BaseAddress)) return;
        _key ??= AgentCredentialStore.TryResolve(_options.AgentKey);
        if (string.IsNullOrWhiteSpace(_key)) return;

        using var claim = new HttpRequestMessage(HttpMethod.Post,
            $"api/agent/machines/{_machineId}/user-session/process-restarts/claim")
        {
            Content = JsonContent.Create(new { sessionId = _sessionId })
        };
        claim.Headers.Add("X-Kairon-Agent-Key", _key);
        using var response = await _http.SendAsync(claim, ct);
        if (!response.IsSuccessStatusCode) return;
        var instructions = await response.Content.ReadFromJsonAsync<List<ProcessRestartInstruction>>(ct) ?? [];

        foreach (var instruction in instructions)
        {
            var outcome = ProcessRestarter.Restart(instruction, _sessionId);
            _logger.LogInformation("kairon-useragent: approved restart of process {ProcessId} {Result}",
                instruction.ProcessId, outcome.Succeeded ? $"succeeded (new process {outcome.NewProcessId})" : "failed: " + outcome.Error);
            using var report = new HttpRequestMessage(HttpMethod.Post,
                $"api/agent/machines/{_machineId}/user-session/process-restarts/{instruction.CommandId}/result")
            {
                Content = JsonContent.Create(new { succeeded = outcome.Succeeded, newProcessId = outcome.NewProcessId, error = outcome.Error })
            };
            report.Headers.Add("X-Kairon-Agent-Key", _key);
            using var _ = await _http.SendAsync(report, ct);
        }
    }
}
