namespace AIDIP.Backend.Models;

public class DiscoveredApplication
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }
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
