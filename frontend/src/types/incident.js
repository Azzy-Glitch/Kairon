/**
 * Shared incident vocabulary.
 *
 * The backend is authoritative for incident state (frontend PRD section 17). These constants exist
 * so the UI can *describe* backend state consistently - never so it can decide it.
 */

export const IncidentStatus = {
  Detected: 'Detected',
  Investigating: 'Investigating',
  Diagnosed: 'Diagnosed',
  Predicted: 'Predicted',
  RecommendationReady: 'RecommendationReady',
  AwaitingApproval: 'AwaitingApproval',
  Remediating: 'Remediating',
  Verifying: 'Verifying',
  Resolved: 'Resolved',
  Failed: 'Failed',
  Rejected: 'Rejected',
  Cancelled: 'Cancelled'
};

/** The operator-facing pipeline, in order. Drives the lifecycle rail in the incident detail view. */
export const LIFECYCLE_STAGES = [
  { key: IncidentStatus.Detected, label: 'Detected' },
  { key: IncidentStatus.Investigating, label: 'Investigating' },
  { key: IncidentStatus.Diagnosed, label: 'Diagnosed' },
  { key: IncidentStatus.Predicted, label: 'Predicted' },
  { key: IncidentStatus.RecommendationReady, label: 'Recommendation' },
  { key: IncidentStatus.AwaitingApproval, label: 'Approval' },
  { key: IncidentStatus.Remediating, label: 'Remediation' },
  { key: IncidentStatus.Verifying, label: 'Verification' },
  { key: IncidentStatus.Resolved, label: 'Resolved' }
];

export const TERMINAL_STATUSES = [
  IncidentStatus.Resolved,
  IncidentStatus.Failed,
  IncidentStatus.Rejected,
  IncidentStatus.Cancelled
];

export const FAILURE_STATUSES = [
  IncidentStatus.Failed,
  IncidentStatus.Rejected,
  IncidentStatus.Cancelled
];

export const Severity = {
  Info: 'Info',
  Low: 'Low',
  Medium: 'Medium',
  High: 'High',
  Critical: 'Critical'
};

/** Highest first - this is the order the incident feed sorts by. */
export const SEVERITY_ORDER = [
  Severity.Critical,
  Severity.High,
  Severity.Medium,
  Severity.Low,
  Severity.Info
];

export const SEVERITY_RANK = {
  [Severity.Critical]: 4,
  [Severity.High]: 3,
  [Severity.Medium]: 2,
  [Severity.Low]: 1,
  [Severity.Info]: 0
};

export const RemediationStatus = {
  None: 'None',
  Proposed: 'Proposed',
  PolicyRejected: 'PolicyRejected',
  AwaitingApproval: 'AwaitingApproval',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Executing: 'Executing',
  Executed: 'Executed',
  Failed: 'Failed',
  Cancelled: 'Cancelled'
};

export const VerificationStatus = {
  NotStarted: 'NotStarted',
  Pending: 'Pending',
  Passed: 'Passed',
  Failed: 'Failed',
  Inconclusive: 'Inconclusive'
};

export const RiskLevel = {
  Low: 'Low',
  Medium: 'Medium',
  High: 'High',
  Critical: 'Critical'
};

/** Every value an async view can be in (frontend PRD section 19). */
export const AsyncState = {
  Idle: 'idle',
  Loading: 'loading',
  Success: 'success',
  Empty: 'empty',
  Error: 'error'
};

export function isTerminal(status) {
  return TERMINAL_STATUSES.includes(status);
}

export function isFailure(status) {
  return FAILURE_STATUSES.includes(status);
}

export function severityRank(severity) {
  return SEVERITY_RANK[severity] ?? 0;
}
