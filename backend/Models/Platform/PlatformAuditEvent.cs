namespace Kairon.Backend.Models.Platform;

/// <summary>Durable, redacted record of security-sensitive platform administration (project
/// creation, credential issuance/revocation, SDK pairing). Ported from Azzy's productization
/// branch, unchanged in shape - this pattern already exists for incidents (IncidentEvent
/// timeline) and generalizes cleanly to platform-level actions.</summary>
public sealed class PlatformAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = "local-operator";
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public string Result { get; set; } = "succeeded";
    public string? Message { get; set; }
    public string? DataJson { get; set; }
}
