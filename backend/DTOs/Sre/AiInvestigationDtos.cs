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

    [JsonPropertyName("log_events")]
    public List<AgentEventEvidenceDto> LogEvents { get; set; } = new();

    [JsonPropertyName("historical_incidents")]
    public List<HistoricalIncidentDto> HistoricalIncidents { get; set; } = new();

    [JsonPropertyName("available_actions")]
    public List<AvailableActionDto> AvailableActions { get; set; } = new();

    /// <summary>Every endpoint seen for this incident's exact scope in the evidence window, with
    /// totals rather than only the error rows, so the model can tell slow server errors without an
    /// application exception (a dependency/downstream pattern) from fast exception-driven ones.</summary>
    [JsonPropertyName("endpoint_breakdown")]
    public List<EndpointSummaryDto> EndpointBreakdown { get; set; } = new();

    /// <summary>The operator-authorized Windows service for this incident's scope and its live state,
    /// when one is enabled. No host name, machine id or credential is included.</summary>
    [JsonPropertyName("remediation_target")]
    public RemediationTargetContextDto? RemediationTarget { get; set; }

    /// <summary>KAIRON's own deterministic trend forecast for this incident (RiskForecaster),
    /// computed from the metric samples above before the AI is asked anything.</summary>
    [JsonPropertyName("risk_forecast")]
    public RiskForecastDto? RiskForecast { get; set; }

    /// <summary>How often this service has had incidents recently, and whether the problem came
    /// back after a restart - the difference between a transient fault and a persistent defect.</summary>
    [JsonPropertyName("recurrence")]
    public RecurrenceContextDto? Recurrence { get; set; }
}

public static class RiskForecastOutcomes
{
    /// <summary>The trend shows the problem continuing or worsening.</summary>
    public const string Elevated = "elevated";
    /// <summary>Enough data, and it does not show the problem getting worse.</summary>
    public const string Stable = "stable";
    /// <summary>Not enough data to project anything.</summary>
    public const string Inconclusive = "inconclusive";
}

public class RiskForecastDto
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = RiskForecastOutcomes.Inconclusive;

    /// <summary>low | medium | high | critical, or "unknown" when inconclusive.</summary>
    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = "unknown";

    [JsonPropertyName("failure_mode")]
    public string FailureMode { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = new();

    /// <summary>"If the observed trend continues for this long" - never a predicted failure time.</summary>
    [JsonPropertyName("horizon_minutes")]
    public int HorizonMinutes { get; set; }

    /// <summary>How well the data supports the forecast (sample count and consistency), 0..1.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("expected_impact")]
    public string ExpectedImpact { get; set; } = string.Empty;

    [JsonPropertyName("preventive_action")]
    public string PreventiveAction { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "trend-baseline";

    [JsonPropertyName("trends")]
    public List<MetricTrendDto> Trends { get; set; } = new();
}

public class MetricTrendDto
{
    [JsonPropertyName("metric")]
    public string Metric { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>Average over the earlier half of the evidence window.</summary>
    [JsonPropertyName("earlier")]
    public double Earlier { get; set; }

    /// <summary>Average over the later half.</summary>
    [JsonPropertyName("recent")]
    public double Recent { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    /// <summary>rising | falling | steady</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "steady";

    /// <summary>Share of later-half samples at or over the threshold, 0..1.</summary>
    [JsonPropertyName("persistence")]
    public double Persistence { get; set; }

    [JsonPropertyName("samples")]
    public int Samples { get; set; }
}

public class RecurrenceContextDto
{
    /// <summary>Other incidents for the same project, environment and service in the last 24 hours.</summary>
    [JsonPropertyName("incidents_last_24h")]
    public int IncidentsLast24h { get; set; }

    /// <summary>A previous incident for this scope was resolved by a restart shortly before this one
    /// was detected - the restart cleared the symptoms only temporarily.</summary>
    [JsonPropertyName("recurred_after_restart")]
    public bool RecurredAfterRestart { get; set; }

    [JsonPropertyName("minutes_since_restart")]
    public double? MinutesSinceRestart { get; set; }

    [JsonPropertyName("previous_remediation")]
    public string? PreviousRemediation { get; set; }
}

public class EndpointSummaryDto
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("requests")]
    public int Requests { get; set; }

    [JsonPropertyName("server_errors")]
    public int ServerErrors { get; set; }

    [JsonPropertyName("client_errors")]
    public int ClientErrors { get; set; }

    [JsonPropertyName("avg_duration_ms")]
    public double AvgDurationMs { get; set; }

    [JsonPropertyName("max_duration_ms")]
    public long MaxDurationMs { get; set; }

    /// <summary>Most common application exception type among this endpoint's errors; null when the
    /// errors carried no exception (e.g. a handled 5xx returned by the application itself).</summary>
    [JsonPropertyName("top_error_type")]
    public string? TopErrorType { get; set; }
}

public class RemediationTargetContextDto
{
    /// <summary>WindowsService or AppProcess: what a restart on this target actually restarts.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "WindowsService";

    [JsonPropertyName("windows_service")]
    public string WindowsService { get; set; } = string.Empty;

    /// <summary>Running/Stopped/... from a read-only local SCM probe, or "Unknown".</summary>
    [JsonPropertyName("service_state")]
    public string ServiceState { get; set; } = "Unknown";

    /// <summary>The incident's telemetry was proven (by the local Agent) to come from the machine
    /// this service runs on.</summary>
    [JsonPropertyName("telemetry_machine_scoped")]
    public bool TelemetryMachineScoped { get; set; }
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

/// <summary>
/// A KAIRON Agent event handed to the AI as its own evidence source (docs/OBSERVABILITY_MIGRATION.md):
/// the full (redacted, but untruncated by CorrelatedSignalSnapshot's 200-char summary) message,
/// so the model can reason about the actual log line or process event, not just the compact
/// symptom sentence CorrelatedSignalDto already carries. Deliberately a distinct type from
/// Kairon.Backend.DTOs.AgentEventDto (the ingestion contract in Kairon.Backend.DTOs, PascalCase)
/// rather than reusing it - the mistake that once made the dashboard's own sparklines silently
/// read undefined (docs/REMEDIATION_VERIFICATION.md) was exactly this: one DTO serving two
/// unrelated wire contracts.
/// </summary>
public class AgentEventEvidenceDto
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("occurrence_count")]
    public int OccurrenceCount { get; set; }
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

    /// <summary>confirmed | likely | possible | unknown - how well the evidence establishes the root cause.</summary>
    [JsonPropertyName("root_cause_certainty")]
    public string RootCauseCertainty { get; set; } = "unknown";

    /// <summary>What a person should check or change next (investigation steps, code or dependency
    /// fixes) - advice, never executed.</summary>
    [JsonPropertyName("next_steps")]
    public List<string> NextSteps { get; set; } = new();

    /// <summary>Every available action the model weighed, with its verdict and why - so "not
    /// recommended" is an explained decision rather than an empty list.</summary>
    [JsonPropertyName("considered_actions")]
    public List<ConsideredActionDto> ConsideredActions { get; set; } = new();

    /// <summary>Set by the AI service so the backend can record which provider produced this.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public class ConsideredActionDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>recommended | not_recommended</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "not_recommended";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
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
