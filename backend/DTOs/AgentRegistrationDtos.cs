using System.ComponentModel.DataAnnotations;

namespace Kairon.Backend.DTOs;

/// <summary>Wire contracts for machine/process discovery (docs/DESKTOP_SHELL.md), ported from
/// Azzy's productization branch, unchanged in shape.</summary>
public sealed class AgentRegistrationDto
{
    public Guid MachineId { get; set; }
    [Required, MaxLength(255)] public string HostName { get; set; } = string.Empty;
    [Required, MaxLength(500)] public string OperatingSystem { get; set; } = string.Empty;
    [Required, MaxLength(50)] public string Architecture { get; set; } = string.Empty;
    [Required, MaxLength(50)] public string AgentVersion { get; set; } = string.Empty;
    [Required, MinLength(32), MaxLength(256)] public string AgentKey { get; set; } = string.Empty;
    [Required, MinLength(32), MaxLength(256)] public string UserAgentKey { get; set; } = string.Empty;
    [MinLength(32), MaxLength(256)] public string? PreviousAgentKey { get; set; }
}

public sealed class AgentHeartbeatDto
{
    public DateTime Timestamp { get; set; }
    [MaxLength(500)] public List<ProcessSnapshotDto> Processes { get; set; } = [];
}

public sealed class ProcessSnapshotDto
{
    [Range(1, int.MaxValue)]
    public int ProcessId { get; set; }
    public DateTime StartedAt { get; set; }
    [Required, MaxLength(255)] public string Name { get; set; } = string.Empty;
    [MaxLength(2000)] public string Executable { get; set; } = string.Empty;
    [MaxLength(50)] public string Runtime { get; set; } = "Unknown";
    [Range(0, 100)] public double CpuPercent { get; set; }
    [Range(0, long.MaxValue)] public long MemoryBytes { get; set; }
}

public sealed record MachineStatusDto(Guid Id, string HostName, string OperatingSystem, string Architecture,
    string AgentVersion, string Status, DateTime RegisteredAt, DateTime LastSeenAt, int RunningApplications,
    string UserSessionStatus, DateTime? LastUserAgentSeenAt);

public sealed record ApplicationInventoryDto(Guid Id, Guid MachineId, string MachineName, int ProcessId,
    string Name, string Executable, string Runtime, double CpuPercent, long MemoryBytes, bool IsRunning,
    DateTime LastSeenAt, string Source, int? ParentProcessId, int? SessionId, string? UserName);

/// <summary>Wire contract for KAIRON.UserAgent's heartbeat - the per-interactive-session
/// counterpart to <see cref="AgentHeartbeatDto"/>. Authenticated the same way (same MachineId,
/// a distinct, UserAgent-scoped key registered by the Windows Service. It reuses the Machine
/// identity without exposing the service's machine credential to interactive users.</summary>
public sealed class UserSessionHeartbeatDto
{
    public DateTime Timestamp { get; set; }
    [Range(0, int.MaxValue)] public int SessionId { get; set; }
    [MaxLength(300)] public string UserName { get; set; } = string.Empty;
    [MaxLength(500)] public List<UserProcessSnapshotDto> Processes { get; set; } = [];
}

public sealed class UserProcessSnapshotDto
{
    [Range(1, int.MaxValue)]
    public int ProcessId { get; set; }
    public DateTime StartedAt { get; set; }
    [Required, MaxLength(255)] public string Name { get; set; } = string.Empty;
    [MaxLength(2000)] public string Executable { get; set; } = string.Empty;
    [MaxLength(50)] public string Runtime { get; set; } = "Unknown";

    /// <summary>Always a real measured sample (processor-time delta over an interval) - the
    /// UserAgent never sends a process until it has two samples, so this is never a fabricated
    /// first-tick value.</summary>
    [Range(0, 100)] public double CpuPercent { get; set; }

    [Range(0, long.MaxValue)] public long MemoryBytes { get; set; }
    public int? ParentProcessId { get; set; }
}
