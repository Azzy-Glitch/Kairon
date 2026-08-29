namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A machine running the KAIRON Agent - basic, zero-code monitoring (docs/DESKTOP_SHELL.md,
/// section 46: "Machine automatically discovered -> Basic Monitoring immediately available").
/// Ported from Azzy's productization branch, unchanged in shape.
/// </summary>
public sealed class Machine
{
    public Guid Id { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 of the Agent's own identity key - proves the same Agent installation is
    /// re-registering/heartbeating, not a different one claiming the same MachineId.</summary>
    public string AgentCredentialHash { get; set; } = string.Empty;

    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Last heartbeat from the per-user-session KAIRON.UserAgent, tracked independently
    /// of <see cref="LastSeenAt"/> (the machine-level heartbeat) so the two components' health can
    /// be reported separately: a machine stays Online purely on its own heartbeat even while no
    /// interactive user session is running the UserAgent, and vice versa.</summary>
    public DateTime? LastUserAgentSeenAt { get; set; }
}
