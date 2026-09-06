using Kairon.Backend.Services.Demo;
namespace Kairon.Backend.Services.Orchestration;
/// <summary>Drives the in-process demo simulator's clock (PRD section 20).</summary>
public class DemoSimulationWorker : BackgroundService
{
    private readonly Demo.ILocalDemoSimulator _simulator;
    private readonly IIncidentProcessingQueue _queue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DemoSimulationWorker> _logger;

    public DemoSimulationWorker(
        Demo.ILocalDemoSimulator simulator,
        IIncidentProcessingQueue queue,
        IConfiguration configuration,
        ILogger<DemoSimulationWorker> logger)
    {
        _simulator = simulator;
        _queue = queue;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickSeconds = _configuration.GetValue("DemoEnvironment:TickSeconds", 3);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, tickSeconds)));

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                var state = _simulator.GetState();
                if (!state.Running && state.Phase == DemoPhase.Normal.ToString())
                    continue;

                await _simulator.TickAsync(stoppingToken);

                // Each tick produces new telemetry, so detection is asked to look again straight
                // away rather than waiting for the slower sweep.
                _queue.TryEnqueue(new IncidentWorkItem(
                    WorkItemKind.EvaluateDetection, state.ProjectId, state.Environment, state.Service));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Demo simulation tick failed: {Error}", Redaction.Describe(ex));
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
