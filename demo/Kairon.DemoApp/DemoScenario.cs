namespace Kairon.DemoApp;

/// <summary>
/// The controlled order-processing failure scenario (PRD section 20).
///
/// Everything here is simulated inside this process: there is no real downstream service, no real
/// thread starvation, and nothing that can escape the demo application. That is deliberate - the
/// scenario has to be dramatic enough to demonstrate the pipeline and harmless enough to run on a
/// judge's laptop.
/// </summary>
public class DemoScenario
{
    private readonly object _gate = new();
    private readonly ILogger<DemoScenario> _logger;

    private bool _retryLoopEnabled;
    private bool _failureSimulationActive;
    private int _workerConcurrency = 16;
    private bool _cacheWarm = true;
    private DateTime? _startedAt;

    private long _queueDepth = 2;
    private long _retriesSinceLastDrain;

    // Each processed order adds this much simulated CPU cost while the retry loop is active.
    private const int RetriesPerFailedOrder = 6;

    public DemoScenario(ILogger<DemoScenario> logger) => _logger = logger;

    public bool RetryLoopEnabled
    {
        get { lock (_gate) return _retryLoopEnabled; }
    }

    public int WorkerConcurrency
    {
        get { lock (_gate) return _workerConcurrency; }
    }

    /// <summary>
    /// Simulated processing of one order. Returns the outcome the endpoint should produce, plus
    /// how long to pretend it took.
    /// </summary>
    public OrderOutcome ProcessOrder()
    {
        lock (_gate)
        {
            if (!_failureSimulationActive)
            {
                _queueDepth = Math.Max(0, _queueDepth - 1);
                return new OrderOutcome(true, LatencyMs: 120, Retries: 0, "Order processed.");
            }

            // With the retry loop active, every order re-calls the (simulated) inventory service
            // several times before giving up. Retries, latency and the queue all climb together,
            // which is what produces the correlated signals the backend is meant to fold into one
            // incident rather than five.
            _retriesSinceLastDrain += RetriesPerFailedOrder;
            _queueDepth += 2;

            var latency = 400 + RetriesPerFailedOrder * 320;

            return new OrderOutcome(
                false,
                LatencyMs: latency,
                Retries: RetriesPerFailedOrder,
                "Order processing retry exhausted while calling inventory service.");
        }
    }

    public long DrainRetries()
    {
        lock (_gate)
        {
            var retries = _retriesSinceLastDrain;
            _retriesSinceLastDrain = 0;
            return retries;
        }
    }

    public long QueueDepth
    {
        get { lock (_gate) return _queueDepth; }
    }

    public DemoControlResponse Execute(string command)
    {
        lock (_gate)
        {
            switch (command)
            {
                case "start-retry-storm":
                    _retryLoopEnabled = true;
                    _failureSimulationActive = true;
                    _startedAt = DateTime.UtcNow;
                    _logger.LogWarning("DEMO: retry loop enabled - order processing will now fail and retry");
                    return Snapshot("Retry-loop scenario started.");

                case "stop":
                    _retryLoopEnabled = false;
                    _failureSimulationActive = false;
                    _queueDepth = 2;
                    _retriesSinceLastDrain = 0;
                    _logger.LogInformation("DEMO: scenario stopped and reset to baseline");
                    return Snapshot("Demo scenario stopped and reset to baseline.");

                case "disable-retry-loop":
                    if (!_retryLoopEnabled)
                        return Snapshot("Retry loop was already disabled.");

                    _retryLoopEnabled = false;
                    _failureSimulationActive = false;
                    _logger.LogInformation("DEMO: retry loop disabled by remediation");
                    return Snapshot("Retry loop disabled. Order processing is recovering.");

                case "restart-service":
                    _queueDepth = 2;
                    _logger.LogInformation("DEMO: service restarted, queue cleared");
                    return Snapshot("Demo service restarted; queue cleared.");

                case "clear-cache":
                    _cacheWarm = true;
                    _logger.LogInformation("DEMO: cache cleared");
                    return Snapshot("Demo cache cleared and repopulating.");

                case "reduce-concurrency":
                    var previous = _workerConcurrency;
                    _workerConcurrency = Math.Max(2, _workerConcurrency / 2);
                    _logger.LogInformation("DEMO: worker concurrency {From} -> {To}", previous, _workerConcurrency);
                    return Snapshot($"Worker concurrency reduced from {previous} to {_workerConcurrency}.");

                case "reset-failure-simulation":
                    _retryLoopEnabled = false;
                    _failureSimulationActive = false;
                    _queueDepth = 2;
                    _retriesSinceLastDrain = 0;
                    _workerConcurrency = 16;
                    _logger.LogInformation("DEMO: failure simulation reset");
                    return Snapshot("Failure simulation reset to healthy baseline.");

                case "health-check":
                    return Snapshot(
                        $"Health check: retryLoop={_retryLoopEnabled}, queue={_queueDepth}, concurrency={_workerConcurrency}");

                default:
                    return new DemoControlResponse
                    {
                        Success = false,
                        Message = $"Unknown demo command '{command}'."
                    };
            }
        }
    }

    public DemoControlResponse GetState()
    {
        lock (_gate)
        {
            return Snapshot("Current demo state.");
        }
    }

    /// <summary>Caller must hold the lock.</summary>
    private DemoControlResponse Snapshot(string message) => new()
    {
        Success = true,
        Message = message,
        Running = _failureSimulationActive,
        Phase = _failureSimulationActive ? "RetryLoop" : "Normal",
        RetryLoopEnabled = _retryLoopEnabled,
        WorkerConcurrency = _workerConcurrency,
        CacheWarm = _cacheWarm,
        FailureSimulationActive = _failureSimulationActive,
        QueueDepth = _queueDepth,
        StartedAt = _startedAt,
        UsingLocalSimulator = false,
        Service = "OrderProcessingService",
        Application = "Kairon.DemoApp",
        Environment = "Demo"
    };
}

public record OrderOutcome(bool Success, int LatencyMs, int Retries, string Message);

/// <summary>
/// Response shape for the control API. Field names match the backend's DemoStateDto so the
/// backend can deserialize it directly.
/// </summary>
public class DemoControlResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool Running { get; set; }
    public string Phase { get; set; } = "Normal";
    public bool RetryLoopEnabled { get; set; }
    public int WorkerConcurrency { get; set; }
    public bool CacheWarm { get; set; }
    public bool FailureSimulationActive { get; set; }
    public double QueueDepth { get; set; }
    public DateTime? StartedAt { get; set; }
    public bool UsingLocalSimulator { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Environment { get; set; } = "Demo";
}
