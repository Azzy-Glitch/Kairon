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
    string AgentVersion, string Status, DateTime RegisteredAt, DateTime LastSeenAt, int RunningApplications);

public sealed record ApplicationInventoryDto(Guid Id, Guid MachineId, string MachineName, int ProcessId,
    string Name, string Executable, string Runtime, double CpuPercent, long MemoryBytes, bool IsRunning,
    DateTime LastSeenAt);
