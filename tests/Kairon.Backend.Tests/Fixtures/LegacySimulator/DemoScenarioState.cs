namespace Kairon.Backend.Services.Demo;

/// <summary>
/// The controlled order-processing retry-loop scenario from PRD section 20. Phases are explicit so
/// the demo is deterministic: the same button always produces the same story.
/// </summary>
public enum DemoPhase
{
    Normal = 0,
    RetryLoop = 1,
    Degraded = 2,
    Recovering = 3
}

/// <summary>Snapshot of the simulated demo environment, safe to return to the UI.</summary>
public class DemoStateDto
{
    public bool Running { get; set; }
    public string Phase { get; set; } = DemoPhase.Normal.ToString();
    public bool RetryLoopEnabled { get; set; }
    public int WorkerConcurrency { get; set; }
    public bool CacheWarm { get; set; }
    public bool FailureSimulationActive { get; set; }

    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double LatencyMs { get; set; }
    public double ErrorRate { get; set; }
    public double RetriesPerMinute { get; set; }
    public double QueueDepth { get; set; }

    public int TicksInPhase { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastTickAt { get; set; }

    /// <summary>True when the backend is simulating in-process because the demo app is not reachable.</summary>
    public bool UsingLocalSimulator { get; set; }

    public string Service { get; set; } = "OrderProcessingService";
    public string Application { get; set; } = "Kairon.DemoApp";
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = "Development";
}

public class DemoCommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Error { get; init; }
    public DemoStateDto? State { get; init; }

    public static DemoCommandResult Ok(string message, DemoStateDto? state = null) =>
        new() { Success = true, Message = message, State = state };

    public static DemoCommandResult Fail(string error) =>
        new() { Success = false, Message = "Demo command failed", Error = error };
}

/// <summary>Commands the demo environment understands. Closed set, matching the demo tools.</summary>
public static class DemoCommands
{
    public const string StartRetryStorm = "start-retry-storm";
    public const string Stop = "stop";
    public const string RestartService = "restart-service";
    public const string ClearCache = "clear-cache";
    public const string DisableRetryLoop = "disable-retry-loop";
    public const string ReduceConcurrency = "reduce-concurrency";
    public const string ResetFailureSimulation = "reset-failure-simulation";
    public const string HealthCheck = "health-check";
}
