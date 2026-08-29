namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A process the Agent found running on a Machine, reported via heartbeat - "basic monitoring"
/// with no SDK involved. Ported from Azzy's productization branch, unchanged in shape.
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
}
