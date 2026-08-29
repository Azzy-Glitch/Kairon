namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A durable, idempotent record of one accepted normalized telemetry event (see
/// PlatformTelemetryService/PlatformTelemetryController) - EventId is the caller-supplied
/// idempotency key; a resend with the same EventId is reported back as a duplicate rather than
/// stored twice. Adapted from Azzy's productization branch. PayloadJson holds the already-
/// redacted event (Redaction.Scrub applied before serialization), bounded to 16000 chars.
/// </summary>
public sealed class TelemetryReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? SourceId { get; set; }
    public DateTime EventTimestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
