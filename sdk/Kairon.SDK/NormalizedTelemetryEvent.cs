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

    /// <summary>This process, on metric events: the Agent independently identifies the process from
    /// the OS, and only then does the backend trust this report of its folder (for KAIRON's
    /// application restart). Matches the Python SDK.</summary>
    public int? ProcessId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }

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
            ProcessId = System.Environment.ProcessId,
            Metadata = ProcessMetadata(),
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

    /// <summary>cpu.scope states what CpuPercent measures: this process, as a share of the machine.</summary>
    internal static Dictionary<string, string> ProcessMetadata()
    {
        var metadata = new Dictionary<string, string> { ["cpu.scope"] = "process" };
        if (System.Environment.ProcessPath is { Length: > 0 } executable) metadata["process.executable"] = executable;
        try { metadata["process.cwd"] = System.Environment.CurrentDirectory; }
        catch (IOException) { } // the folder was removed; KAIRON simply cannot offer a restart
        return metadata;
    }

    private static string Bound(string? value, int limit, string fallback = "")
    {
        var effective = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return effective.Length <= limit ? effective : effective[..limit];
    }
}

/// <summary>Outcome of one batch send. <see cref="RetryAfter"/> is set only for a 429, already
/// bounded by <see cref="KaironTelemetryClient.MaxRetryAfter"/>.</summary>
internal sealed record NormalizedBatchResult(int Delivered, int Failed, string? Message, TimeSpan? RetryAfter = null)
{
    public bool RateLimited => RetryAfter.HasValue;
}

/// <summary>
/// The backend's machine-proof scope for a batch: one project, one service (falling back to the
/// application when blank, compared ordinally) and one environment (blank meaning Development,
/// compared case-insensitively) - mirrored exactly from PlatformTelemetryController so the SDK
/// never attaches a proof the backend would reject as a mixed batch.
/// </summary>
internal readonly record struct NormalizedBatchScope(Guid ProjectId, string Service, string Environment)
{
    internal static NormalizedBatchScope Of(NormalizedTelemetryEvent item) => new(
        item.ProjectId,
        string.IsNullOrWhiteSpace(item.Service) ? item.Application : item.Service,
        (string.IsNullOrWhiteSpace(item.Environment) ? "Development" : item.Environment).ToUpperInvariant());

    internal static bool IsHomogeneous(IReadOnlyList<NormalizedTelemetryEvent> events)
    {
        if (events.Count == 0) return false;
        var first = Of(events[0]);
        for (var i = 1; i < events.Count; i++)
            if (Of(events[i]) != first) return false;
        return true;
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
