using Microsoft.Extensions.Options;

namespace KAIRON.Agent;

public sealed class AgentWorker : BackgroundService
{
    private readonly MachineIdentityStore _identityStore;
    private readonly ProcessCollector _processes;
    private readonly BackendAgentClient _backend;
    private readonly ILogger<AgentWorker> _logger;
    private readonly TimeSpan _interval;

    public AgentWorker(MachineIdentityStore identityStore, ProcessCollector processes, BackendAgentClient backend,
        IOptions<AgentOptions> options, ILogger<AgentWorker> logger)
    {
        _identityStore = identityStore;
        _processes = processes;
        _backend = backend;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.CollectionIntervalSeconds, 2, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await _identityStore.LoadOrCreateAsync(stoppingToken);
        var registered = false;
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                if (!registered)
                {
                    registered = await _backend.RegisterAsync(identity, stoppingToken);
                    if (!registered) _logger.LogWarning("KAIRON Agent registration was rejected; retrying.");
                }

                if (registered)
                {
                    var accepted = await _backend.HeartbeatAsync(identity, _processes.Collect(), stoppingToken);
                    if (!accepted)
                    {
                        registered = false;
                        _logger.LogWarning("KAIRON Agent heartbeat was rejected; registration will be retried.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                registered = false;
                _logger.LogWarning("KAIRON backend is unavailable; monitoring will retry without affecting applications.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
