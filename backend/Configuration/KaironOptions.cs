using Kairon.Backend.Models.Sre;

namespace Kairon.Backend.Configuration;

/// <summary>
/// Detection thresholds (PRD section 7: "Rules must be configurable"). Everything the deterministic
/// detection engine uses is here, so tuning detection never requires a code change.
/// </summary>
public class DetectionOptions
{
    public const string SectionName = "Detection";

    public bool Enabled { get; set; } = true;

    /// <summary>Look-back window for evaluating metric samples.</summary>
    public int EvaluationWindowSeconds { get; set; } = 120;

    /// <summary>A threshold rule only fires once a breach has persisted for this long.</summary>
    public int SustainedBreachSeconds { get; set; } = 20;

    /// <summary>Minimum samples in the window before a metric rule is allowed to fire.</summary>
    public int MinimumSamples { get; set; } = 2;

    /// <summary>Suppression window for an identical signal (PRD section 7: dedup + cooldown).</summary>
    public int CooldownSeconds { get; set; } = 60;

    /// <summary>Signals for the same service inside this window fold into one incident (PRD section 8).</summary>
    public int CorrelationWindowSeconds { get; set; } = 300;

    public double CpuPercentThreshold { get; set; } = 80;
    public double MemoryPercentThreshold { get; set; } = 85;
    public double LatencyMsThreshold { get; set; } = 1000;

    /// <summary>Fraction of requests failing, 0..1.</summary>
    public double ErrorRateThreshold { get; set; } = 0.10;

    /// <summary>Retries per minute above which a retry storm is declared.</summary>
    public double RetryStormPerMinute { get; set; } = 30;

    /// <summary>Requests per minute above which a burst is declared.</summary>
    public double RequestBurstPerMinute { get; set; } = 600;

    /// <summary>Queue depth above which backlog is declared.</summary>
    public double QueueDepthThreshold { get; set; } = 50;

    /// <summary>Standard deviations from the window baseline that count as a sudden deviation.</summary>
    public double DeviationSigma { get; set; } = 3.0;

    /// <summary>Identical errors on one endpoint within the window that count as "repeated errors".</summary>
    public int RepeatedErrorCount { get; set; } = 5;

    /// <summary>Severity escalation - breach ratio (observed/threshold) at which severity climbs.</summary>
    public double HighSeverityRatio { get; set; } = 1.25;
    public double CriticalSeverityRatio { get; set; } = 1.6;
}

/// <summary>Remediation policy configuration (PRD section 11 and 12).</summary>
public class RemediationOptions
{
    public const string SectionName = "Remediation";

    /// <summary>Master switch. When false, actions can be proposed but never executed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// PRD section 13: "Initial implementation requires approval for every remediation."
    /// Left configurable but defaulted to true; the policy refuses to execute without approval.
    /// </summary>
    public bool RequireApprovalForEveryAction { get; set; } = true;

    /// <summary>Risk level above which the policy refuses the action outright.</summary>
    public RiskLevel MaxAllowedRisk { get; set; } = RiskLevel.High;

    /// <summary>Optional allowlist. Empty means "every registered tool is permitted".</summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>Tools that are never permitted regardless of the allowlist.</summary>
    public List<string> BlockedTools { get; set; } = new();

    /// <summary>Environments in which execution is permitted at all.</summary>
    public List<string> AllowedEnvironments { get; set; } = new() { "Development", "Demo", "Staging" };

    public int ExecutionTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum executions per incident, so a loop cannot hammer the target.</summary>
    public int MaxActionsPerIncident { get; set; } = 5;
}

/// <summary>Verification configuration (PRD section 13).</summary>
public class VerificationOptions
{
    public const string SectionName = "Verification";

    /// <summary>
    /// How long to let the environment react before any post-remediation telemetry counts.
    /// Samples from inside this period are discarded rather than averaged: they measure the
    /// recovery in progress, not the state it recovered to.
    /// </summary>
    public int SettleSeconds { get; set; } = 12;

    /// <summary>Window of settled post-remediation telemetry to average.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// How long to keep waiting past the settle period for enough settled samples to arrive
    /// before giving up and reporting the verification inconclusive.
    /// </summary>
    public int MaxWaitSeconds { get; set; } = 30;

    /// <summary>Settled samples required before a comparison is considered meaningful.</summary>
    public int MinimumSamples { get; set; } = 2;

    /// <summary>Fraction of breached metrics that must recover for verification to pass.</summary>
    public double RequiredRecoveryScore { get; set; } = 0.6;
}

/// <summary>AI orchestration configuration (PRD section 9).</summary>
public class AiOrchestrationOptions
{
    public const string SectionName = "AiOrchestration";

    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// How long a newly detected incident is allowed to accumulate correlated signals before the
    /// first AI investigation runs.
    ///
    /// A degradation rarely announces itself with its most telling signal first: an incident often
    /// trips on repeated errors seconds before the retry storm actually causing them becomes
    /// measurable. Investigating instantly produces a diagnosis of the symptom that happened to
    /// arrive first, which then has to be revised. Waiting briefly costs a few seconds of
    /// mean-time-to-diagnosis and buys a first answer that is usually the right one.
    /// </summary>
    public int InvestigationDelaySeconds { get; set; } = 15;

    /// <summary>Bound on evidence handed to the model (PRD section 10: evidence must be bounded).</summary>
    public int MaxMetricSamples { get; set; } = 30;
    public int MaxRelatedErrors { get; set; } = 20;
    public int MaxHistoricalIncidents { get; set; } = 5;
    public int MaxEvidencePayloadChars { get; set; } = 16000;

    /// <summary>Capacity of the incident processing queue. Full queue drops work rather than blocking ingestion.</summary>
    public int QueueCapacity { get; set; } = 512;
}

/// <summary>Authorization for state-changing SRE endpoints (PRD section 19).</summary>
public class SreSecurityOptions
{
    public const string SectionName = "SreSecurity";

    /// <summary>When true, approve/reject/execute endpoints require a valid operator key.</summary>
    public bool RequireOperatorKey { get; set; } = false;

    public string HeaderName { get; set; } = "X-Kairon-Operator-Key";

    /// <summary>Server-side only. Never returned by any endpoint and never logged.</summary>
    public string? OperatorKey { get; set; }
}

/// <summary>Where the demo-environment remediation tools point (PRD section 20).</summary>
public class DemoEnvironmentOptions
{
    public const string SectionName = "DemoEnvironment";

    public string BaseUrl { get; set; } = "http://localhost:5080";
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When the demo app is not running, tools fall back to an in-process simulator so the whole
    /// lifecycle still demonstrates end to end. Disable to make tool failures real.
    /// </summary>
    public bool AllowLocalSimulatorFallback { get; set; } = true;
}
