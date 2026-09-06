namespace Kairon.Backend.Models;

public class Metric
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }
    public DateTime Timestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? ResponseTimeMs { get; set; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }
    public string Environment { get; set; } = "Development";

    // --- Autonomous SRE additions (nullable/defaulted, so existing rows and existing
    // SDK payloads keep working unchanged). These carry the retry-storm and backlog
    // signals the detection engine needs (PRD section 7).
    public long? RetryCount { get; set; }
    public long? QueueDepth { get; set; }
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
}
