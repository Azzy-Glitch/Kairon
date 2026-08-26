namespace Kairon.Backend.Models;

public class EnrichedContext
{
    public Guid ProjectId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Environment { get; set; } = "Development";
    public Incident? CurrentIncident { get; set; }

    public List<Incident> RecentIncidents { get; set; } = new();
    public List<Metric> RecentMetrics { get; set; } = new();

    public int FailureCount { get; set; }
    public double AverageLatency { get; set; }
    public double RecentErrorRate { get; set; }

    public Dictionary<string, object> AdditionalContext { get; set; } = new();
}