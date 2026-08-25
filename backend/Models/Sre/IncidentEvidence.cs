namespace AIDIP.Backend.Models.Sre;

/// <summary>
/// A bounded, persisted snapshot of the evidence package handed to the AI service (PRD section 10).
/// Persisting it makes the AI conclusion auditable: an operator can see exactly what the model saw.
/// </summary>
public class IncidentEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public SreIncident? Incident { get; set; }

    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>metrics | errors | correlated-signals | history | endpoint | service.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    /// <summary>Bounded JSON payload. The collector caps size before persisting.</summary>
    public string PayloadJson { get; set; } = "{}";

    public int ItemCount { get; set; }
}

public static class EvidenceKinds
{
    public const string IncidentMetadata = "incident-metadata";
    public const string RecentMetrics = "recent-metrics";
    public const string RelatedErrors = "related-errors";
    public const string CorrelatedSignals = "correlated-signals";
    public const string HistoricalIncidents = "historical-incidents";
    public const string ServiceIdentity = "service-identity";
}
