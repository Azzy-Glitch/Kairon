namespace Kairon.Backend.DTOs;

/// <summary>
/// Wire shape the KAIRON Agent posts to POST /api/telemetry/events. PascalCase, matching every
/// other telemetry payload's convention (TelemetryPayload, MetricDto) - one contract for
/// anything a .NET process sends, whether it is an SDK-instrumented app or the Agent.
/// </summary>
public class AgentEventDto
{
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
    public string Severity { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int OccurrenceCount { get; set; } = 1;
    public string? MetadataJson { get; set; }
}
