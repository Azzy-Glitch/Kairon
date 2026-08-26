using System.Net.Http.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Services.Audit;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Demo;

/// <summary>
/// The only channel through which remediation can affect the demo environment (PRD section 20:
/// "The remediation must affect only the controlled demo environment").
///
/// It talks to the demo application over a fixed, closed set of HTTP control commands. There is no
/// shell, no process control, and no way to express an arbitrary operation. When the demo app is
/// not running, it falls back to the in-process simulator so the lifecycle still demonstrates end
/// to end.
/// </summary>
public interface IDemoEnvironmentClient
{
    Task<DemoCommandResult> SendAsync(string command, CancellationToken cancellationToken = default);
    Task<DemoStateDto> GetStateAsync(CancellationToken cancellationToken = default);
}

public class DemoEnvironmentClient : IDemoEnvironmentClient
{
    private readonly HttpClient _http;
    private readonly ILocalDemoSimulator _simulator;
    private readonly DemoEnvironmentOptions _options;
    private readonly ILogger<DemoEnvironmentClient> _logger;

    public DemoEnvironmentClient(
        HttpClient http,
        ILocalDemoSimulator simulator,
        IOptions<DemoEnvironmentOptions> options,
        ILogger<DemoEnvironmentClient> logger)
    {
        _http = http;
        _simulator = simulator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DemoCommandResult> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        // The local simulator wins while it is actively running: if the operator started the
        // scenario in-process, remediation must act on that same scenario.
        if (_simulator.IsRunning)
            return _simulator.Execute(command);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var response = await _http.PostAsJsonAsync(
                $"kairon-control/{command}", new { }, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<DemoStateDto>(cancellationToken: timeout.Token);
                return DemoCommandResult.Ok($"Demo app accepted '{command}'.", payload);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return DemoCommandResult.Fail(
                $"Demo app returned {(int)response.StatusCode} for '{command}': {Redaction.Scrub(Bound(body))}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_options.AllowLocalSimulatorFallback)
                return DemoCommandResult.Fail($"Demo app unreachable: {Redaction.Describe(ex)}");

            _logger.LogInformation(
                "Demo app unreachable ({Error}); executing '{Command}' against the in-process simulator instead",
                Redaction.Describe(ex), command);

            return _simulator.Execute(command);
        }
    }

    public async Task<DemoStateDto> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (_simulator.IsRunning)
            return _simulator.GetState();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var state = await _http.GetFromJsonAsync<DemoStateDto>("kairon-control/state", timeout.Token);
            if (state is not null)
            {
                state.UsingLocalSimulator = false;
                return state;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Demo app state unavailable ({Error}); reporting simulator state", Redaction.Describe(ex));
        }

        return _simulator.GetState();
    }

    private static string Bound(string value) => value.Length <= 300 ? value : value[..300] + "...";
}
