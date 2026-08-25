namespace AIDIP.Backend.Models;

/// <summary>
/// A single request-scoped telemetry record captured by the AIDIP SDK. One row per instrumented
/// HTTP call. This is intentionally NOT the Autonomous SRE incident aggregate - see
/// <see cref="AIDIP.Backend.Models.Sre.SreIncident"/> for that. Keeping this type unchanged is
/// what preserves the existing SDK contract, the /api/telemetry endpoints, and the Telemetry
/// Monitor screen (PRD section 15).
/// </summary>
public class Incident
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? RequestId { get; set; }
    public string Environment { get; set; } = "Development";
    public string Severity { get; set; } = "Error";
    public bool Resolved { get; set; }
    public string? MetadataJson { get; set; }

    // --- Autonomous SRE additions (nullable, additive). The SDK already sends
    // ApplicationName; persisting it lets detection and correlation attribute
    // telemetry to a service without guessing.
    public string? Application { get; set; }
    public string? Service { get; set; }

    /// <summary>Set when this telemetry row has been folded into an SreIncident.</summary>
    public Guid? SreIncidentId { get; set; }
}
