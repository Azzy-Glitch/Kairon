namespace AIDIP.Backend.DTOs;

public class MetricDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? ResponseTimeMs { get; set; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }
    public string Environment { get; set; } = "Development";
    public Dictionary<string, double>? CustomMetrics { get; set; }
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
}