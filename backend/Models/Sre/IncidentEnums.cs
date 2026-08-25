namespace AIDIP.Backend.Models.Sre;

/// <summary>
/// Lifecycle states for an SRE incident, per the AIDIP Autonomous AI SRE PRD (§5).
/// The happy path runs Detected -> ... -> Resolved; Failed/Rejected/Cancelled are
/// terminal failure paths reachable from most working states.
/// </summary>
public enum IncidentStatus
{
    Detected = 0,
    Investigating = 1,
    Diagnosed = 2,
    Predicted = 3,
    RecommendationReady = 4,
    AwaitingApproval = 5,
    Remediating = 6,
    Verifying = 7,
    Resolved = 8,

    Failed = 100,
    Rejected = 101,
    Cancelled = 102
}

public enum IncidentSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum RemediationStatus
{
    None = 0,
    Proposed = 1,
    PolicyRejected = 2,
    AwaitingApproval = 3,
    Approved = 4,
    Rejected = 5,
    Executing = 6,
    Executed = 7,
    Failed = 8,
    Cancelled = 9
}

public enum VerificationStatus
{
    NotStarted = 0,
    Pending = 1,
    Passed = 2,
    Failed = 3,
    Inconclusive = 4
}

/// <summary>Deterministic detector that produced a signal (PRD §7).</summary>
public enum DetectionRuleKind
{
    CpuThreshold = 0,
    MemoryThreshold = 1,
    ErrorRateThreshold = 2,
    LatencyThreshold = 3,
    MetricDeviation = 4,
    RepeatedErrors = 5,
    RequestBurst = 6,
    RetryStorm = 7
}
