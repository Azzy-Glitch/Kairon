namespace Kairon.UserAgent;

/// <summary>
/// Configuration for KAIRON.UserAgent (docs/DESKTOP_SHELL.md) - the per-interactive-session
/// counterpart to the KAIRON.Agent Windows Service. It authenticates with a distinct scoped key
/// so interactive users never receive the service's machine credential.
/// </summary>
public class UserAgentOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>Public sentinel only; it is never sent. The service-generated scoped key is read
    /// from ProgramData at runtime and missing credentials defer heartbeats safely.</summary>
    public string AgentKey { get; set; } = "kairon-useragent-default-key-change-me";

    public int TimeoutSeconds { get; set; } = 5;

    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>Upper bound on process snapshots sent per heartbeat - a busy interactive session
    /// must not turn into an unbounded payload. The busiest (highest-CPU) processes are kept when
    /// the cap is hit.</summary>
    public int MaxProcesses { get; set; } = 300;
}
