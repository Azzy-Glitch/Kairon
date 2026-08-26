namespace Kairon.Backend.DTOs.Sre;

// Operator-facing DTOs. These are what the React dashboard consumes; entities are never
// serialized directly, so the storage shape can change without breaking the frontend.

public class SreIncidentSummaryDto
{
    public Guid Id { get; set; }
    public string IncidentKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AffectedComponent { get; set; } = string.Empty;
    public string AffectedEndpoint { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string RemediationState { get; set; } = string.Empty;
    public string VerificationState { get; set; } = string.Empty;
    public int SignalCount { get; set; }
    public List<string> Symptoms { get; set; } = new();

    /// <summary>Short description for the feed row - the AI summary if present, else the first symptom.</summary>
    public string ShortDescription { get; set; } = string.Empty;

    /// <summary>True while the incident is waiting on a human decision.</summary>
    public bool NeedsApproval { get; set; }
}

public class SreIncidentDetailDto : SreIncidentSummaryDto
{
    public AiDiagnosisDto? Diagnosis { get; set; }
    public PredictionDto? Prediction { get; set; }
    public List<RecommendationDto> Recommendations { get; set; } = new();
    public List<RemediationActionDto> Actions { get; set; } = new();
    public List<VerificationResultDto> Verifications { get; set; } = new();
    public List<IncidentEventDto> Timeline { get; set; } = new();
    public List<CorrelatedSignalDto> CorrelatedSignals { get; set; } = new();
    public List<Guid> TelemetryReferences { get; set; } = new();
    public string? FailureReason { get; set; }
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// True when signals correlated in after the current diagnosis was produced. The UI surfaces
    /// this rather than quietly showing a conclusion that no longer matches the evidence below it.
    /// </summary>
    public bool DiagnosisStale { get; set; }

    /// <summary>Lifecycle states this incident may legally move to next. Drives UI affordances.</summary>
    public List<string> AllowedNextStates { get; set; } = new();
}

/// <summary>
/// AI-derived diagnosis. Kept in its own object so the UI can render it visually distinct from
/// raw telemetry, per frontend PRD section 7.
/// </summary>
public class AiDiagnosisDto
{
    public string Summary { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public List<string> ContributingFactors { get; set; } = new();
    public List<string> Evidence { get; set; } = new();

    /// <summary>Model self-estimate, 0..1. Not objective certainty (AI PRD section 9).</summary>
    public double? Confidence { get; set; }

    public List<string> AffectedComponents { get; set; } = new();

    /// <summary>Always "ai-estimate", so the UI never has to infer provenance.</summary>
    public string Source { get; set; } = "ai-estimate";

    public string? Provider { get; set; }
    public string? Model { get; set; }
    public DateTime? GeneratedAt { get; set; }
}

public class PredictionDto
{
    public string PredictedFailure { get; set; } = string.Empty;
    public string EstimatedRisk { get; set; } = string.Empty;
    public string PotentialImpact { get; set; } = string.Empty;
    public string Source { get; set; } = "ai-prediction";
}

public class RecommendationDto
{
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ExpectedOutcome { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;

    /// <summary>False when the named action is not a registered tool - the UI greys it out.</summary>
    public bool IsRegisteredTool { get; set; }

    /// <summary>Policy verdict text when the recommendation was refused before reaching an operator.</summary>
    public string? PolicyNote { get; set; }
}

public class RemediationActionDto
{
    public Guid Id { get; set; }
    public string ActionKey { get; set; } = string.Empty;
    public Guid IncidentId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ExpectedOutcome { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? PolicyDecision { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ExecutionResult { get; set; }
    public string? ExecutionError { get; set; }
    public VerificationResultDto? VerificationResult { get; set; }
}

public class VerificationResultDto
{
    public Guid Id { get; set; }
    public Guid? ActionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public double RecoveryScore { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public List<MetricComparisonDto> Comparisons { get; set; } = new();
}

public class MetricComparisonDto
{
    public string Metric { get; set; } = string.Empty;
    public double? Before { get; set; }
    public double? After { get; set; }
    public string Unit { get; set; } = string.Empty;
    public bool Improved { get; set; }
    public bool MeetsThreshold { get; set; }
    public double? Threshold { get; set; }
}

public class IncidentEventDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? PreviousState { get; set; }
    public string? NewState { get; set; }
    public string? ActionId { get; set; }
    public string? Result { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class IncidentEvidenceDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public DateTime CollectedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

// --- Requests ---

public class ApproveActionRequest
{
    /// <summary>Operator identity recorded in the audit trail. Required.</summary>
    public string ApprovedBy { get; set; } = string.Empty;

    public string? Note { get; set; }
}

public class RejectActionRequest
{
    public string RejectedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public class CancelIncidentRequest
{
    public string CancelledBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

// --- Aggregates for the dashboard ---

public class SreDashboardDto
{
    public int ActiveIncidents { get; set; }
    public int AwaitingApproval { get; set; }
    public int Remediating { get; set; }
    public int ResolvedLast24h { get; set; }
    public Dictionary<string, int> SeverityDistribution { get; set; } = new();
    public Dictionary<string, int> StatusDistribution { get; set; } = new();
    public SreIncidentSummaryDto? TopIncident { get; set; }
    public LiveMetricsDto Metrics { get; set; } = new();
    public SystemHealthDto Health { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class LiveMetricsDto
{
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? LatencyMs { get; set; }
    public double? ErrorRate { get; set; }
    public double? RetriesPerMinute { get; set; }
    public double? QueueDepth { get; set; }
    public DateTime? SampledAt { get; set; }
    public List<MetricSampleDto> Recent { get; set; } = new();
}

public class SystemHealthDto
{
    public bool Backend { get; set; } = true;
    public bool Database { get; set; }
    public bool AiService { get; set; }
    public bool DetectionEnabled { get; set; }
    public bool RemediationEnabled { get; set; }
    public string AiMode { get; set; } = "unknown";
}

public class RemediationToolDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }
    public bool AllowedByPolicy { get; set; }
    public string? PolicyNote { get; set; }
}
