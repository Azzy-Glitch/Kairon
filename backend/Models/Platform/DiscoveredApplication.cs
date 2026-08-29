namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A process found running on a Machine, reported via heartbeat - "basic monitoring" with no SDK
/// involved. Ported from Azzy's productization branch, unchanged in shape except for the
/// multi-source fields below (docs/DESKTOP_SHELL.md: KAIRON.Agent Windows Service vs
/// KAIRON.UserAgent).
/// </summary>
public sealed class DiscoveredApplication
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public int ProcessId { get; set; }
    public DateTime ProcessStartedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string Runtime { get; set; } = "Unknown";
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public bool IsRunning { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Which component reported this row: "MachineAgent" (the LocalService Windows
    /// Service - limited cross-session visibility) or "UserAgent" (the per-interactive-session
    /// collector - full visibility into that user's own processes). Rows are scoped by Source when
    /// a heartbeat marks stale entries not-running, so one source's heartbeat can never clobber the
    /// other's data for the same machine.</summary>
    public string Source { get; set; } = "MachineAgent";

    public int? ParentProcessId { get; set; }

    /// <summary>Windows Terminal Services session id the process belongs to - only ever populated
    /// by the UserAgent, which can read it for processes in its own interactive session.</summary>
    public int? SessionId { get; set; }

    /// <summary>The interactive user the reporting UserAgent is running as (e.g. "HOST\alice").
    /// Null for MachineAgent-sourced rows.</summary>
    public string? UserName { get; set; }
}
