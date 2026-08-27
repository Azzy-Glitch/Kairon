namespace Kairon.Backend.Models;

/// <summary>
/// A normalized event reported by the KAIRON Agent (docs/OBSERVABILITY_MIGRATION.md) - a log
/// pattern match or a process lifecycle/resource event. Deliberately one table with an
/// <see cref="EventType"/> discriminator rather than separate log/process tables: both are the
/// same shape (what happened, on which service, when, with what evidence), matching the
/// canonical, language-agnostic event model the migration is built around. <see cref="Metric"/>
/// (fixed numeric gauge columns) and <see cref="Incident"/> (HTTP-call-shaped) stay untouched -
/// this is a new source, not a repurposing of either.
/// </summary>
public class AgentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>LogPatternMatch, ProcessStarted, ProcessStopped, ProcessCrash, ProcessHighResource.</summary>
    public string EventType { get; set; } = string.Empty;

    public string Environment { get; set; } = "Production";
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }

    /// <summary>Info, Warning, Error, Critical - free text, matching Incident.Severity's own convention.</summary>
    public string Severity { get; set; } = "Info";

    /// <summary>
    /// The evidence text - a matched (and deduplicated) log line/group, or a description of the
    /// process event. Redacted server-side before being persisted (see TelemetryController):
    /// unlike Metric/Incident rows, this is the first ingestion path that can carry raw,
    /// unredacted-by-default free text, so it does not get the same free pass.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Where this came from - a log file path, or a process name.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>How many times this exact (deduplicated) event was observed in the reporting window.</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>Structured extras: CPU%/memory for a process event, match count for a log event, etc.</summary>
    public string? MetadataJson { get; set; }
}
