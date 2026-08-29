namespace Kairon.UserAgent;

/// <summary>
/// Configuration for KAIRON.UserAgent (docs/DESKTOP_SHELL.md) - the per-interactive-session
/// counterpart to the KAIRON.Agent Windows Service. Endpoint/AgentKey default to the exact same
/// values as agent/Kairon.Agent/AgentOptions.cs so both components authenticate against the same
/// Machine identity out of the box, with no separate pairing step required.
/// </summary>
public class UserAgentOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>Must match Kairon.Agent's AgentOptions.AgentKey default - see the comment there.
    /// Not a new weakness introduced by the UserAgent: both components have always shared one
    /// static default credential in this codebase; rotating it per-install is a separate,
    /// pre-existing hardening task, out of scope here.</summary>
    public string AgentKey { get; set; } = "kairon-agent-default-key-change-me";

    public int TimeoutSeconds { get; set; } = 5;

    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>Upper bound on process snapshots sent per heartbeat - a busy interactive session
    /// must not turn into an unbounded payload. The busiest (highest-CPU) processes are kept when
    /// the cap is hit.</summary>
    public int MaxProcesses { get; set; } = 300;
}
