using System.Text.Json.Serialization;

namespace Kairon.Backend.DTOs.Sre;

// The wire contract between the backend and the FastAPI AI service (AI PRD sections 6, 7, 15).
// snake_case on the wire, because that is what the existing Python service speaks.

/// <summary>Bounded evidence handed to the AI service. Never the whole database (PRD section 9).</summary>
public class EvidencePackageDto
{
    [JsonPropertyName("incident")]
    public IncidentContextDto Incident { get; set; } = new();

    [JsonPropertyName("recent_metrics")]
    public List<MetricSampleDto> RecentMetrics { get; set; } = new();

    [JsonPropertyName("related_errors")]
    public List<RelatedErrorDto> RelatedErrors { get; set; } = new();

    [JsonPropertyName("correlated_signals")]
    public List<CorrelatedSignalDto> CorrelatedSignals { get; set; } = new();

    [JsonPropertyName("historical_incidents")]
    public List<HistoricalIncidentDto> HistoricalIncidents { get; set; } = new();

    [JsonPropertyName("available_actions")]
    public List<AvailableActionDto> AvailableActions { get; set; } = new();
}

public class IncidentContextDto
{
    [JsonPropertyName("incident_id")]
    public string IncidentId { get; set; } = string.Empty;

    [JsonPropertyName("incident_key")]
    public string IncidentKey { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("affected_component")]
    public string AffectedComponent { get; set; } = string.Empty;

    [JsonPropertyName("affected_endpoint")]
    public string AffectedEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("detected_at")]
    public DateTime DetectedAt { get; set; }

    [JsonPropertyName("symptoms")]
    public List<string> Symptoms { get; set; } = new();
}

public class MetricSampleDto
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("cpu_percent")]
    public double? CpuPercent { get; set; }

    [JsonPropertyName("memory_percent")]
    public double? MemoryPercent { get; set; }

    [JsonPropertyName("response_time_ms")]
    public double? ResponseTimeMs { get; set; }

    [JsonPropertyName("request_count")]
    public long RequestCount { get; set; }

    [JsonPropertyName("error_count")]
    public long ErrorCount { get; set; }

    [JsonPropertyName("retry_count")]
    public long? RetryCount { get; set; }

    [JsonPropertyName("queue_depth")]
    public long? QueueDepth { get; set; }
}

public class RelatedErrorDto
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("error_type")]
    public string? ErrorType { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}

public class CorrelatedSignalDto
{
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonPropertyName("metric")]
    public string Metric { get; set; } = string.Empty;

    [JsonPropertyName("symptom")]
    public string Symptom { get; set; } = string.Empty;

    [JsonPropertyName("observed")]
    public double? Observed { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("detected_at")]
    public DateTime DetectedAt { get; set; }
}

public class HistoricalIncidentDto
{
    [JsonPropertyName("incident_key")]
    public string IncidentKey { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("root_cause")]
    public string? RootCause { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("detected_at")]
    public DateTime DetectedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// The closed set of remediation tools the model is allowed to name. Anything outside this list is
/// rejected by policy before it can reach an executor (PRD section 12).
/// </summary>
public class AvailableActionDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = string.Empty;
}

// --- Structured AI response (AI PRD section 7). ---

public class InvestigationResultDto
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("root_cause")]
    public string RootCause { get; set; } = string.Empty;

    [JsonPropertyName("contributing_factors")]
    public List<string> ContributingFactors { get; set; } = new();

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = new();

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "medium";

    [JsonPropertyName("affected_components")]
    public List<string> AffectedComponents { get; set; } = new();

    [JsonPropertyName("predicted_failure")]
    public string PredictedFailure { get; set; } = string.Empty;

    [JsonPropertyName("estimated_risk")]
    public string EstimatedRisk { get; set; } = "medium";

    [JsonPropertyName("recommendations")]
    public List<AiRecommendationDto> Recommendations { get; set; } = new();

    /// <summary>Set by the AI service so the backend can record which provider produced this.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public class AiRecommendationDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("expected_outcome")]
    public string ExpectedOutcome { get; set; } = string.Empty;

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = "medium";

    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; set; }
}
