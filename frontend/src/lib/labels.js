/**
 * Copy/naming layer (redesign brief section 5). Maps every system identifier the backend actually
 * emits to a human, sentence-case string. No raw identifier should reach the DOM as a user-facing
 * label - import from here at every render site instead.
 *
 * Every key below was pulled from the real backend source, not guessed:
 *   - actionLabel/actionDescription: backend/Services/Remediation/IRemediationTool.cs (DemoToolNames)
 *   - ruleLabel: backend/Services/Detection/DetectionRules.cs (RuleId)
 *   - signalLabel: the same file's MetricName assignments
 *   - phaseLabel: backend/Models/Sre/IncidentEnums.cs (IncidentStatus) and
 *     backend/Models/Sre/IncidentEvent.cs (IncidentEventTypes)
 *   - sourceLabel: frontend/src/lib/source.js's SdkSource values
 */

export const actionLabel = {
  RestartDemoService: 'Restart the service',
  ClearDemoCache: 'Clear the cache',
  DisableDemoRetryLoop: 'Turn off the retry loop',
  ReduceDemoWorkerConcurrency: 'Reduce worker concurrency',
  ResetDemoFailureSimulation: 'Reset the failure simulation',
  RunHealthCheck: 'Run a health check'
};

export const actionDescription = {
  RestartDemoService: 'Restarts the affected service process.',
  ClearDemoCache: "Clears the service's in-memory cache.",
  DisableDemoRetryLoop: 'Stops the runaway retry loop that is amplifying load.',
  ReduceDemoWorkerConcurrency: 'Lowers concurrent worker threads to relieve resource pressure.',
  ResetDemoFailureSimulation: 'Resets the simulated failure condition back to normal.',
  RunHealthCheck: "Runs a fresh health check against the service's control API."
};

export const signalLabel = {
  cpu: 'CPU usage',
  memory: 'Memory usage',
  latency: 'Response time',
  errorRate: 'Error rate',
  retries: 'Retry rate',
  requests: 'Request rate',
  errors: 'Errors',
  logPattern: 'Log pattern match',
  processCrash: 'Process crash',
  processHighResource: 'Process resource usage',
  queue: 'Queue depth'
};

export const ruleLabel = {
  'cpu-threshold': 'CPU over limit',
  'memory-threshold': 'Memory over limit',
  'latency-threshold': 'Response time over limit',
  'error-rate-threshold': 'Error rate over limit',
  'retry-storm': 'Retry storm',
  'request-burst': 'Request burst',
  'metric-deviation': 'Unusual deviation from baseline',
  'repeated-errors': 'Repeated errors on one endpoint',
  'log-pattern-match': 'Recognized error pattern in logs',
  'process-crash': 'Process crashed',
  'process-high-resource': 'Process using excess resources',
  'queue-backlog': 'Queue backlog',
  // Kept for compatibility with the brief's own listed examples, which don't exactly match the
  // live RuleId strings above (no "errorRate"/"detection-engine"/"correlation-engine" RuleId
  // exists in the backend - these three are event actors, not rule ids; mapped in sourceActorLabel
  // below instead, but left here too in case a future rule reuses one of these exact strings).
  errorRate: 'Error rate'
};

/** Actors that appear on IncidentEvents (audit trail) - system components, not detection rules. */
export const sourceActorLabel = {
  'detection-engine': 'Detection',
  'correlation-engine': 'Signal correlation',
  'ai-orchestrator': 'Kairon AI',
  'verification-service': 'Recovery check',
  'remediation-executor': 'Remediation'
};

/** IncidentStatus (the incident's current lifecycle phase) and IncidentEventTypes (individual
 * audit-trail entries) share most of their vocabulary, so one map covers both. */
export const phaseLabel = {
  Detected: 'Detected',
  Correlated: 'Related signals found',
  Investigating: 'Investigation started',
  AiRequestStarted: 'Investigation started',
  AiBudgetExceeded: 'AI budget exceeded',
  Diagnosed: 'Diagnosis complete',
  Predicted: 'Prediction complete',
  Recommended: 'Recommendation ready',
  RecommendationReady: 'Recommendation ready',
  PolicyEvaluated: 'Policy check complete',
  AwaitingApproval: 'Awaiting approval',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Executing: 'Remediation running',
  Remediating: 'Remediation running',
  Executed: 'Remediation applied',
  Verifying: 'Checking recovery',
  Verified: 'Recovery checked',
  Resolved: 'Resolved',
  Failed: 'Failed',
  Cancelled: 'Cancelled'
};

/** Retry-loop-active / degraded style compound phase strings some UI code has historically shown
 * verbatim - "RetryLoop / Degraded" in the brief's own required-mappings table. */
export const compoundPhaseLabel = {
  RetryLoop: 'Retry loop active',
  Degraded: 'Degraded'
};

function toLabel(map, value, fallback) {
  if (value === null || value === undefined) return fallback ?? '';
  return map[value] ?? fallback ?? String(value);
}

export function getActionLabel(actionType) {
  return toLabel(actionLabel, actionType, actionType);
}

export function getActionDescription(actionType) {
  return toLabel(actionDescription, actionType, '');
}

export function getSignalLabel(metricName) {
  return toLabel(signalLabel, metricName, metricName);
}

export function getRuleLabel(ruleId) {
  return toLabel(ruleLabel, ruleId, ruleId);
}

export function getSourceActorLabel(actor) {
  return toLabel(sourceActorLabel, actor, actor);
}

export function getPhaseLabel(phase) {
  return toLabel(phaseLabel, phase, phase);
}

/** Severity always sorts Critical -> High -> Medium -> Low everywhere (brief sections 5 and 11). */
export const SEVERITY_ORDER = ['Critical', 'High', 'Medium', 'Low'];

export function compareSeverity(a, b) {
  const ai = SEVERITY_ORDER.indexOf(a);
  const bi = SEVERITY_ORDER.indexOf(b);
  return (ai === -1 ? SEVERITY_ORDER.length : ai) - (bi === -1 ? SEVERITY_ORDER.length : bi);
}

export function sortBySeverity(items, getSeverity = (x) => x.severity) {
  return [...items].sort((a, b) => compareSeverity(getSeverity(a), getSeverity(b)));
}

/** Relative primary timestamp ("4 min ago"), absolute available for a title attribute (brief
 * section 5). One format across the whole app. */
export function formatRelativeTime(isoString) {
  if (!isoString) return '';
  const date = new Date(isoString);
  const diffMs = Date.now() - date.getTime();
  const diffSec = Math.round(diffMs / 1000);
  if (diffSec < 5) return 'Just now';
  if (diffSec < 60) return `${diffSec}s ago`;
  const diffMin = Math.round(diffSec / 60);
  if (diffMin < 60) return `${diffMin} min ago`;
  const diffHour = Math.round(diffMin / 60);
  if (diffHour < 24) return `${diffHour}h ago`;
  const diffDay = Math.round(diffHour / 24);
  return `${diffDay}d ago`;
}

export function formatAbsoluteTime(isoString) {
  if (!isoString) return '';
  return new Date(isoString).toLocaleString();
}
