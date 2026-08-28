namespace AIDIP.Backend.Models;

/// <summary>Durable, redacted record of security-sensitive platform administration.</summary>
public sealed class PlatformAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Category { get; set; } = "platform";
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = "local-operator";
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public string Result { get; set; } = "succeeded";
    public string? Message { get; set; }
    public string? DataJson { get; set; }
}
