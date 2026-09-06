namespace Kairon.SDK;

/// <summary>
/// SDK configuration. Every knob here is about collection and delivery; there is deliberately no
/// AI credential, no database setting and no remediation option, because the SDK is a telemetry
/// collector and nothing more (PRD section 4.1).
/// </summary>
public class KaironOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? ApiKey { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }
    public bool EnableTelemetry { get; set; } = true;
    public bool CaptureRequestBody { get; set; } = false;
    public bool CaptureResponseBody { get; set; } = false;

    /// <summary>Application name reported with every payload. Falls back to the entry assembly name.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Logical service name used by the backend for correlation. Defaults to
    /// <see cref="ApplicationName"/> when unset.
    /// </summary>
    public string? ServiceName { get; set; }

    public string? Environment { get; set; }

    /// <summary>
    /// Per-request timeout for a telemetry send. Kept short on purpose: a slow collector must not
    /// hold SDK resources, and a dropped telemetry item is always cheaper than a delayed one.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Capacity of the in-process send queue. When full, the oldest queued item is dropped rather
    /// than blocking the application (PRD section 17: telemetry must be bounded and fail open).
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Maximum captured request/response body size, in characters.</summary>
    public int MaxBodyCharacters { get; set; } = 2000;

    /// <summary>Fraction of successful requests to report, 0..1. Errors are always reported.</summary>
    public double SuccessSampleRate { get; set; } = 1.0;

    /// <summary>Emits process CPU/memory/throughput metrics on an interval.</summary>
    public bool EnableMetrics { get; set; } = true;

    public int MetricsIntervalSeconds { get; set; } = 10;

    /// <summary>Paths that are never instrumented, e.g. health probes that would swamp telemetry.</summary>
    public List<string> IgnoredPathPrefixes { get; set; } = new() { "/health", "/healthz", "/metrics", "/favicon.ico" };
}
