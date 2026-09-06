using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Demo;

/// <summary>
/// An in-process, controlled simulation of the demo order-processing service (PRD section 20).
///
/// The demo application is the primary source of the scenario, but a judge should be able to run
/// the whole acceptance test with only the backend and the dashboard running. When the demo app
/// is not reachable, this stands in for it: it emits real telemetry rows through the same tables
/// the SDK writes to, so detection, correlation, AI and remediation all run against genuine data
/// rather than a scripted UI animation.
///
/// It only ever touches its own demo project id and environment, and it never executes anything
/// outside this process.
/// </summary>
public interface ILocalDemoSimulator
{
    DemoStateDto GetState();
    DemoCommandResult Execute(string command);
    Task TickAsync(CancellationToken cancellationToken);
    bool IsRunning { get; }
}

public class LocalDemoSimulator : ILocalDemoSimulator
{
    // Steady-state baselines for a healthy service.
    private const double BaselineCpu = 24;
    private const double BaselineMemory = 46;
    private const double BaselineLatency = 120;
    private const double BaselineQueue = 2;

    // Where a fully developed retry storm lands.
    private const double StormCpu = 95;
    private const double StormMemory = 82;
    private const double StormLatency = 2400;
    private const double StormRetries = 90;
    private const double StormQueue = 85;
    private const double StormErrorRate = 0.34;

    // Ticks to ramp into degradation and to recover out of it. At the default 3s tick that is
    // roughly 24s of visible degradation and 15s of visible recovery - long enough to watch,
    // short enough for a hackathon demo.
    private const int RampTicks = 8;
    private const int RecoveryTicks = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalDemoSimulator> _logger;
    private readonly object _gate = new();

    private DemoPhase _phase = DemoPhase.Normal;
    private bool _running;
    private bool _retryLoopEnabled;
    private int _workerConcurrency = 16;
    private bool _cacheWarm = true;
    private bool _failureSimulationActive;
    private int _ticksInPhase;
    private DateTime? _startedAt;
    private DateTime? _lastTickAt;

    private double _cpu = BaselineCpu;
    private double _memory = BaselineMemory;
    private double _latency = BaselineLatency;
    private double _errorRate;
    private double _retries;
    private double _queue = BaselineQueue;

    // Deterministic jitter. A fixed seed keeps the demo reproducible while still looking like
    // real telemetry rather than a straight line.
    private readonly Random _jitter = new(20260817);

    public LocalDemoSimulator(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<LocalDemoSimulator> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    private Guid ProjectId =>
        Guid.TryParse(_configuration["Kairon:ProjectId"], out var id)
            ? id
            : Guid.Empty;

    public DemoStateDto GetState()
    {
        lock (_gate)
        {
            return Snapshot();
        }
    }

    public DemoCommandResult Execute(string command)
    {
        lock (_gate)
        {
            switch (command)
            {
                case DemoCommands.StartRetryStorm:
                    _running = true;
                    _retryLoopEnabled = true;
                    _failureSimulationActive = true;
                    _phase = DemoPhase.RetryLoop;
                    _ticksInPhase = 0;
                    _startedAt = DateTime.UtcNow;
                    _logger.LogInformation("Demo simulator: retry storm started");
                    return DemoCommandResult.Ok("Retry-loop scenario started.", Snapshot());

                case DemoCommands.Stop:
                    _running = false;
                    _retryLoopEnabled = false;
                    _failureSimulationActive = false;
                    _phase = DemoPhase.Normal;
                    _ticksInPhase = 0;
                    ResetMetricsToBaseline();
                    _logger.LogInformation("Demo simulator: stopped and reset to baseline");
                    return DemoCommandResult.Ok("Demo simulation stopped and reset to baseline.", Snapshot());

                case DemoCommands.DisableRetryLoop:
                    if (!_retryLoopEnabled)
                        return DemoCommandResult.Ok("Retry loop was already disabled.", Snapshot());

                    _retryLoopEnabled = false;
                    _phase = DemoPhase.Recovering;
                    _ticksInPhase = 0;
                    _logger.LogInformation("Demo simulator: retry loop disabled, recovering");
                    return DemoCommandResult.Ok("Retry loop disabled. Service is recovering.", Snapshot());

                case DemoCommands.RestartService:
                    _phase = DemoPhase.Recovering;
                    _ticksInPhase = 0;
                    _queue = BaselineQueue;
                    _memory = BaselineMemory;
                    _logger.LogInformation("Demo simulator: service restarted");
                    return DemoCommandResult.Ok("Demo service restarted; queue and memory cleared.", Snapshot());

                case DemoCommands.ClearCache:
                    _cacheWarm = true;
                    // A cold-start cost is the honest outcome of clearing a cache, so the demo
                    // shows it rather than pretending the action is free.
                    _latency = Math.Max(_latency, BaselineLatency * 1.4);
                    _logger.LogInformation("Demo simulator: cache cleared");
                    return DemoCommandResult.Ok("Demo cache cleared and repopulating.", Snapshot());

                case DemoCommands.ReduceConcurrency:
                    var previous = _workerConcurrency;
                    _workerConcurrency = Math.Max(2, _workerConcurrency / 2);
                    if (_phase == DemoPhase.Degraded || _phase == DemoPhase.RetryLoop)
                    {
                        // Fewer workers relieves CPU but does not fix a retry loop; the queue
                        // keeps growing. That is the point - a wrong-but-plausible remediation
                        // must be allowed to fail verification.
                        _cpu = Math.Max(BaselineCpu, _cpu * 0.75);
                        _queue = Math.Min(StormQueue * 1.2, _queue * 1.15);
                    }
                    _logger.LogInformation("Demo simulator: worker concurrency {From} -> {To}", previous, _workerConcurrency);
                    return DemoCommandResult.Ok(
                        $"Worker concurrency reduced from {previous} to {_workerConcurrency}.", Snapshot());

                case DemoCommands.ResetFailureSimulation:
                    _failureSimulationActive = false;
                    _retryLoopEnabled = false;
                    _phase = DemoPhase.Recovering;
                    _ticksInPhase = 0;
                    _logger.LogInformation("Demo simulator: failure simulation reset");
                    return DemoCommandResult.Ok("Failure simulation reset.", Snapshot());

                case DemoCommands.HealthCheck:
                    return DemoCommandResult.Ok(
                        $"Health check: phase={_phase}, cpu={Math.Round(_cpu, 1)}%, latency={Math.Round(_latency)}ms, " +
                        $"retries={Math.Round(_retries)}/min, queue={Math.Round(_queue)}",
                        Snapshot());

                default:
                    return DemoCommandResult.Fail($"Unknown demo command '{command}'.");
            }
        }
    }

    /// <summary>
    /// Advances the simulation one step and persists a metric sample (plus telemetry rows when the
    /// scenario is failing requests). Called by the hosted service on a timer.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        DemoStateDto snapshot;
        int errorsThisTick;

        lock (_gate)
        {
            if (!_running && _phase == DemoPhase.Normal)
                return;

            Advance();
            _lastTickAt = DateTime.UtcNow;
            snapshot = Snapshot();
            errorsThisTick = (int)Math.Round(_errorRate * 20);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        const int requestsPerTick = 20;

        db.Metrics.Add(new Metric
        {
            Id = Guid.NewGuid(),
            ProjectId = snapshot.ProjectId,
            Timestamp = now,
            CpuPercent = Math.Round(snapshot.CpuPercent, 1),
            MemoryPercent = Math.Round(snapshot.MemoryPercent, 1),
            ResponseTimeMs = Math.Round(snapshot.LatencyMs, 0),
            RequestCount = requestsPerTick,
            ErrorCount = errorsThisTick,
            RetryCount = (long)Math.Round(snapshot.RetriesPerMinute / 20),
            QueueDepth = (long)Math.Round(snapshot.QueueDepth),
            Environment = snapshot.Environment,
            Application = snapshot.Application,
            Service = snapshot.Service,
            Component = snapshot.Service
        });

        // Emit real telemetry rows for the failing requests, so the repeated-errors and error-rate
        // rules have something to find and the Telemetry Monitor shows the same story.
        for (var i = 0; i < Math.Min(errorsThisTick, 5); i++)
        {
            db.Incidents.Add(new Incident
            {
                Id = Guid.NewGuid(),
                ProjectId = snapshot.ProjectId,
                Timestamp = now,
                Endpoint = "/api/orders/process",
                Method = "POST",
                StatusCode = 503,
                DurationMs = (long)Math.Round(snapshot.LatencyMs),
                ErrorType = "DownstreamTimeoutException",
                ErrorMessage = "Order processing retry exhausted while calling inventory service",
                Environment = snapshot.Environment,
                Severity = "Error",
                Application = snapshot.Application,
                Service = snapshot.Service
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Moves the simulated metrics one tick along the current phase.</summary>
    private void Advance()
    {
        _ticksInPhase++;

        switch (_phase)
        {
            case DemoPhase.RetryLoop:
            {
                var progress = Math.Min(1.0, (double)_ticksInPhase / RampTicks);
                _cpu = Lerp(BaselineCpu, StormCpu, progress);
                _memory = Lerp(BaselineMemory, StormMemory, progress);
                _latency = Lerp(BaselineLatency, StormLatency, progress);
                _retries = Lerp(0, StormRetries, progress);
                _queue = Lerp(BaselineQueue, StormQueue, progress);
                _errorRate = Lerp(0, StormErrorRate, progress);

                if (progress >= 1.0)
                {
                    _phase = DemoPhase.Degraded;
                    _ticksInPhase = 0;
                }

                break;
            }

            case DemoPhase.Degraded:
            {
                // Fully degraded and staying there until someone remediates. Slight upward drift
                // on the queue, because a backlog does not stabilise on its own.
                _cpu = Clamp(StormCpu + Jitter(2), 0, 100);
                _memory = Clamp(StormMemory + Jitter(1.5), 0, 100);
                _latency = StormLatency + Jitter(120);
                _retries = StormRetries + Jitter(6);
                _queue = Math.Min(StormQueue * 1.4, _queue + 1.5);
                _errorRate = Clamp(StormErrorRate + Jitter(0.02), 0, 1);
                break;
            }

            case DemoPhase.Recovering:
            {
                var progress = Math.Min(1.0, (double)_ticksInPhase / RecoveryTicks);
                _cpu = Lerp(_cpu, BaselineCpu, progress);
                _memory = Lerp(_memory, BaselineMemory, progress);
                _latency = Lerp(_latency, BaselineLatency, progress);
                _retries = Lerp(_retries, 0, progress);
                _queue = Lerp(_queue, BaselineQueue, progress);
                _errorRate = Lerp(_errorRate, 0, progress);

                if (progress >= 1.0)
                {
                    _phase = DemoPhase.Normal;
                    _ticksInPhase = 0;
                    ResetMetricsToBaseline();
                }

                break;
            }

            case DemoPhase.Normal:
            default:
            {
                _cpu = Clamp(BaselineCpu + Jitter(3), 0, 100);
                _memory = Clamp(BaselineMemory + Jitter(2), 0, 100);
                _latency = BaselineLatency + Jitter(15);
                _retries = 0;
                _queue = Math.Max(0, BaselineQueue + Jitter(1));
                _errorRate = 0;
                break;
            }
        }
    }

    private void ResetMetricsToBaseline()
    {
        _cpu = BaselineCpu;
        _memory = BaselineMemory;
        _latency = BaselineLatency;
        _errorRate = 0;
        _retries = 0;
        _queue = BaselineQueue;
    }

    private DemoStateDto Snapshot() => new()
    {
        Running = _running,
        Phase = _phase.ToString(),
        RetryLoopEnabled = _retryLoopEnabled,
        WorkerConcurrency = _workerConcurrency,
        CacheWarm = _cacheWarm,
        FailureSimulationActive = _failureSimulationActive,
        CpuPercent = Math.Round(_cpu, 1),
        MemoryPercent = Math.Round(_memory, 1),
        LatencyMs = Math.Round(_latency, 0),
        ErrorRate = Math.Round(_errorRate, 3),
        RetriesPerMinute = Math.Round(_retries, 1),
        QueueDepth = Math.Round(_queue, 0),
        TicksInPhase = _ticksInPhase,
        StartedAt = _startedAt,
        LastTickAt = _lastTickAt,
        UsingLocalSimulator = true,
        ProjectId = ProjectId,
        Environment = "Development"
    };

    private static double Lerp(double from, double to, double t) => from + (to - from) * Math.Clamp(t, 0, 1);

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    private double Jitter(double magnitude) => (_jitter.NextDouble() - 0.5) * 2 * magnitude;
}
