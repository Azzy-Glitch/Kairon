namespace Kairon.Agent;

/// <summary>
/// KAIRON Agent configuration (docs/OBSERVABILITY_MIGRATION.md). Zero-code monitoring: the
/// target application needs no SDK, no source change - only these settings, pointing the Agent
/// at a log directory and/or a process name to watch.
/// </summary>
public class AgentOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? ApiKey { get; set; }
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = "Production";
    public string Application { get; set; } = "kairon-agent";
    public string? Service { get; set; }

    /// <summary>Per-send timeout, kept short so a slow/unreachable backend never backs up the
    /// Agent's own polling loops.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    // --- Log tailing ---------------------------------------------------------------------

    public bool EnableLogTailing { get; set; } = true;

    /// <summary>Directory to watch for the one rotation scheme this Agent supports: dated daily
    /// files named "{LogFilePrefix}{yyyyMMdd}.txt", the same pattern backend/Program.cs's own
    /// Serilog config already uses.</summary>
    public string? LogDirectory { get; set; }

    public string LogFilePrefix { get; set; } = "kairon-";

    public int LogPollIntervalSeconds { get; set; } = 3;

    /// <summary>An identical (post-dedup) log entry seen again within this window is folded into
    /// the earlier event's occurrence count instead of sent again.</summary>
    public int LogDedupWindowSeconds { get; set; } = 30;

    /// <summary>Upper bound on distinct log events sent per poll - a runaway log must not turn
    /// into a flood against the backend.</summary>
    public int MaxLogEventsPerPoll { get; set; } = 20;

    // --- Process watching ------------------------------------------------------------------

    public bool EnableProcessWatch { get; set; } = true;

    /// <summary>Process name to watch (no ".exe" suffix), e.g. "Kairon.DemoApp".</summary>
    public string? TargetProcessName { get; set; }

    public int ProcessPollIntervalSeconds { get; set; } = 5;

    public double HighCpuPercentThreshold { get; set; } = 85.0;
}
