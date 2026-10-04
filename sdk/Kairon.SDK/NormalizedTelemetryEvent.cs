using Kairon.SDK.Models;

namespace Kairon.SDK;

/// <summary>
/// The backend's existing /api/v1/telemetry/events contract. Machine identity is deliberately
/// absent: only the backend can derive it from an Agent-confirmed proof for the exact body.
/// </summary>
internal sealed class NormalizedTelemetryEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public DateTime Timestamp { get; init; }
    public string EventType { get; init; } = "http";
    public string Severity { get; init; } = "Information";
    public string Source { get; init; } = "dotnet-sdk";
    public string Application { get; init; } = "Unknown";
    public string Service { get; init; } = "Unknown";
    public string Environment { get; init; } = "Production";
    public string Runtime { get; init; } = ".NET " + System.Environment.Version;
    public string SourceVersion { get; init; } = "1.1.0";
    public string? RequestId { get; init; }
    public string? Message { get; init; }
    public string? ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    public NormalizedHttpContext? HttpContext { get; init; }
    public NormalizedResourceMetrics? ResourceMetrics { get; init; }

    internal static NormalizedTelemetryEvent From(TelemetryPayload payload, KaironOptions options)
    {
        var application = Bound(payload.ApplicationName, 200, KaironIdentity.ResolveApplication(options));
        return new NormalizedTelemetryEvent
        {
            ProjectId = options.ProjectId,
            Timestamp = payload.Timestamp,
            EventType = "http",
            Severity = payload.StatusCode >= 500 || !string.IsNullOrEmpty(payload.ExceptionType) ? "Error" : "Information",
            Application = application,
            Service = Bound(payload.Service, 200, KaironIdentity.ResolveService(options)),
            Environment = Bound(payload.Environment, 100, KaironIdentity.ResolveEnvironment(options)),
            RequestId = Bound(payload.RequestId, 200),
            Message = Bound(payload.Error, 4000),
            ExceptionType = Bound(payload.ExceptionType, 500),
            StackTrace = Bound(payload.StackTrace, 8000),
            HttpContext = new NormalizedHttpContext
            {
                Endpoint = Bound(payload.Endpoint, 500),
                Method = Bound(payload.Method, 10, "GET"),
                StatusCode = payload.StatusCode,
                DurationMs = Math.Max(0, payload.Duration)
            }
        };
    }

    internal static NormalizedTelemetryEvent From(MetricPayload payload, KaironOptions options)
    {
        var application = Bound(payload.Application, 200, KaironIdentity.ResolveApplication(options));
        return new NormalizedTelemetryEvent
        {
            ProjectId = options.ProjectId,
            Timestamp = payload.Timestamp,
            EventType = "metric",
            Application = application,
            Service = Bound(payload.Service, 200, KaironIdentity.ResolveService(options)),
            Environment = Bound(payload.Environment, 100, KaironIdentity.ResolveEnvironment(options)),
            ResourceMetrics = new NormalizedResourceMetrics
            {
                CpuPercent = payload.CpuPercent,
                MemoryPercent = payload.MemoryPercent,
                ResponseTimeMs = payload.ResponseTimeMs,
                RequestCount = payload.RequestCount,
                ErrorCount = payload.ErrorCount,
                RetryCount = payload.RetryCount,
                QueueDepth = payload.QueueDepth
            }
        };
    }

    private static string Bound(string? value, int limit, string fallback = "")
    {
        var effective = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return effective.Length <= limit ? effective : effective[..limit];
    }
}

internal sealed class NormalizedHttpContext
{
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "GET";
    public int StatusCode { get; init; }
    public long DurationMs { get; init; }
}

internal sealed class NormalizedResourceMetrics
{
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? ResponseTimeMs { get; init; }
    public long RequestCount { get; init; }
    public long ErrorCount { get; init; }
    public long? RetryCount { get; init; }
    public long? QueueDepth { get; init; }
}
