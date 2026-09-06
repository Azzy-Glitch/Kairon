namespace Kairon.SDK.Models;

/// <summary>
/// Process-level metrics emitted on an interval. Field names match the backend's MetricDto so the
/// payload deserializes directly, with no translation layer.
/// </summary>
public class MetricPayload
{
    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? ResponseTimeMs { get; set; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }

    /// <summary>Retries observed since the last emit. Reported by the host application.</summary>
    public long? RetryCount { get; set; }

    /// <summary>Pending work depth. Reported by the host application.</summary>
    public long? QueueDepth { get; set; }

    public string Environment { get; set; } = "Production";
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
}
