using System.ComponentModel.DataAnnotations;

namespace AIDIP.Backend.DTOs;

public sealed class NormalizedTelemetryBatchDto
{
    [Required, MinLength(1), MaxLength(200)]
    public List<NormalizedTelemetryEventDto> Events { get; set; } = [];
}

public sealed class NormalizedTelemetryEventDto
{
    public Guid EventId { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; }
    [Required, MaxLength(50)] public string EventType { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string Severity { get; set; } = "Information";
    [Required, MaxLength(50)] public string Source { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string Application { get; set; } = string.Empty;
    [MaxLength(200)] public string Service { get; set; } = string.Empty;
    [MaxLength(100)] public string Environment { get; set; } = "Development";
    [MaxLength(255)] public string Host { get; set; } = string.Empty;
    public int? ProcessId { get; set; }
    [MaxLength(50)] public string Runtime { get; set; } = "Unknown";
    [MaxLength(200)] public string InstallationId { get; set; } = string.Empty;
    [MaxLength(50)] public string SourceVersion { get; set; } = string.Empty;
    [MaxLength(200)] public string? CorrelationId { get; set; }
    [MaxLength(200)] public string? TraceId { get; set; }
    [MaxLength(200)] public string? RequestId { get; set; }
    [MaxLength(4000)] public string? Message { get; set; }
    [MaxLength(500)] public string? ExceptionType { get; set; }
    [MaxLength(8000)] public string? StackTrace { get; set; }
    public HttpTelemetryContextDto? HttpContext { get; set; }
    public DependencyTelemetryContextDto? DependencyContext { get; set; }
    public ResourceTelemetryMetricsDto? ResourceMetrics { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public sealed class HttpTelemetryContextDto
{
    [MaxLength(500)] public string Endpoint { get; set; } = string.Empty;
    [MaxLength(10)] public string Method { get; set; } = "GET";
    [Range(0, 599)] public int StatusCode { get; set; }
    [Range(0, long.MaxValue)] public long DurationMs { get; set; }
}

public sealed class DependencyTelemetryContextDto
{
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(500)] public string Target { get; set; } = string.Empty;
    public bool Success { get; set; }
    [Range(0, long.MaxValue)] public long DurationMs { get; set; }
}

public sealed class ResourceTelemetryMetricsDto
{
    [Range(0, 100)] public double? CpuPercent { get; set; }
    [Range(0, 100)] public double? MemoryPercent { get; set; }
    [Range(0, double.MaxValue)] public double? ResponseTimeMs { get; set; }
    public long RequestCount { get; set; }
    public long ErrorCount { get; set; }
    public long? RetryCount { get; set; }
    public long? QueueDepth { get; set; }
}

public sealed record NormalizedTelemetryResultDto(int Accepted, int Duplicates, int Rejected, IReadOnlyList<Guid> AcceptedEventIds);
