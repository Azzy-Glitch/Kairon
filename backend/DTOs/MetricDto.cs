namespace Kairon.Backend.DTOs;

public class MetricDto
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
    public Dictionary<string, double>? CustomMetrics { get; set; }

    // Autonomous SRE signals. All optional, so existing callers that post only CPU/memory keep
    // working exactly as before.
    public long? RetryCount { get; set; }
    public long? QueueDepth { get; set; }
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
}

public class CreateMetricRequest
{
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? ResponseTimeMs { get; set; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }
    public string Environment { get; set; } = "Development";
    public Dictionary<string, double>? CustomMetrics { get; set; }
    public long? RetryCount { get; set; }
    public long? QueueDepth { get; set; }
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
}
