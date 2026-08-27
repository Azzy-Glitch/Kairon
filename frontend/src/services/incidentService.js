import {
  IncidentStatus,
  LIFECYCLE_STAGES,
  RemediationStatus,
  VerificationStatus,
  isFailure,
  isTerminal,
  severityRank
} from '../types/incident';

/**
 * Presentation logic derived from backend state.
 *
 * Everything here is a pure function of what the backend returned. It shapes and labels; it never
 * decides. In particular nothing in this file can make an incident look resolved when the backend
 * has not said so (frontend PRD sections 13 and 17).
 */

/** Sorts a feed so the worst active incident is first, then the most recently updated. */
export function sortIncidents(incidents = []) {
  return [...incidents].sort((a, b) => {
    const aTerminal = isTerminal(a.status);
    const bTerminal = isTerminal(b.status);
    if (aTerminal !== bTerminal) return aTerminal ? 1 : -1;

    // Waiting on a human is the most actionable thing on the board.
    const aWaiting = a.status === IncidentStatus.AwaitingApproval;
    const bWaiting = b.status === IncidentStatus.AwaitingApproval;
    if (aWaiting !== bWaiting) return aWaiting ? -1 : 1;

    const bySeverity = severityRank(b.severity) - severityRank(a.severity);
    if (bySeverity !== 0) return bySeverity;

    return new Date(b.updatedAt) - new Date(a.updatedAt);
  });
}

/**
 * Builds the lifecycle rail. Each stage is done, active, failed or pending, so the UI can render
 * progress without re-deriving the state machine.
 */
export function buildLifecycle(incident) {
  if (!incident) return [];

  const currentIndex = LIFECYCLE_STAGES.findIndex((s) => s.key === incident.status);
  const failed = isFailure(incident.status);

  // A failed or cancelled incident stopped somewhere; the timeline tells us where.
  const stoppedAt = failed ? lastReachedStageIndex(incident) : currentIndex;

  return LIFECYCLE_STAGES.map((stage, index) => {
    if (failed && index === stoppedAt + 1) {
      return { ...stage, state: 'failed', label: labelForFailure(incident.status) };
    }

    if (index < stoppedAt) return { ...stage, state: 'done' };
    if (index === stoppedAt) return { ...stage, state: failed ? 'done' : 'active' };
    return { ...stage, state: 'pending' };
  });
}

function labelForFailure(status) {
  if (status === IncidentStatus.Rejected) return 'Rejected';
  if (status === IncidentStatus.Cancelled) return 'Cancelled';
  return 'Failed';
}

/** How far a failed incident actually got, read from its own audit timeline. */
function lastReachedStageIndex(incident) {
  const reached = (incident.timeline || [])
    .map((event) => LIFECYCLE_STAGES.findIndex((s) => s.key === event.newState))
    .filter((index) => index >= 0);

  return reached.length ? Math.max(...reached) : 0;
}

/** The action currently waiting on a human decision, if any. */
export function pendingAction(incident) {
  return (incident?.actions || []).find((a) => a.status === RemediationStatus.AwaitingApproval) || null;
}

export function executedActions(incident) {
  return (incident?.actions || []).filter((a) =>
    [RemediationStatus.Executing, RemediationStatus.Executed, RemediationStatus.Failed].includes(a.status)
  );
}

export function latestVerification(incident) {
  const verifications = incident?.verifications || [];
  if (!verifications.length) return null;

  return [...verifications].sort((a, b) => new Date(b.startedAt) - new Date(a.startedAt))[0];
}

/**
 * Whether the UI may show "Resolved".
 *
 * Deliberately strict: it requires the backend's own Resolved status. The frontend does not get to
 * conclude an incident is over because the metrics look better (frontend PRD section 13).
 */
export function isResolvedByBackend(incident) {
  return incident?.status === IncidentStatus.Resolved;
}

export function verificationFailed(incident) {
  const verification = latestVerification(incident);
  return (
    verification?.status === VerificationStatus.Failed ||
    verification?.status === VerificationStatus.Inconclusive
  );
}

/** Human-readable confidence. Always phrased as an estimate, never as certainty. */
export function formatConfidence(confidence) {
  if (confidence === null || confidence === undefined) return 'not reported';
  return `${Math.round(confidence * 100)}% (model estimate)`;
}

export function confidenceBand(confidence) {
  if (confidence === null || confidence === undefined) return 'unknown';
  if (confidence >= 0.85) return 'high';
  if (confidence >= 0.6) return 'moderate';
  return 'low';
}

export function formatMetricValue(value, unit) {
  if (value === null || value === undefined) return '--';
  const rounded = Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
  return `${rounded}${unit || ''}`;
}

export function formatTime(value) {
  if (!value) return '--';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '--' : date.toLocaleTimeString();
}

export function formatDateTime(value) {
  if (!value) return '--';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '--' : date.toLocaleString();
}

export function relativeTime(value) {
  if (!value) return '--';

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--';

  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/** Percentage change between two metric readings, for the verification table. */
export function percentChange(before, after) {
  if (before === null || before === undefined || after === null || after === undefined) return null;
  if (before === 0) return after === 0 ? 0 : null;
  return Math.round(((after - before) / before) * 100);
}

/**
 * Groups a list of incidents by service (frontend PRD section 24: services health).
 *
 * There is no separate service registry to query, so a service this list has never seen an
 * incident for is not something the frontend can know about - this is "services with observed
 * activity", not a full inventory (frontend PRD section 28: do not fabricate unavailable data).
 * Shared by the Services page and the Overview services table so both read the same definition of
 * a service's health.
 */
export function groupByService(incidents = []) {
  const map = new Map();

  for (const incident of incidents) {
    const name = incident.service || 'Unknown service';
    if (!map.has(name)) {
      map.set(name, { name, incidents: [] });
    }
    map.get(name).incidents.push(incident);
  }

  return [...map.values()]
    .map((svc) => {
      const active = svc.incidents.filter((i) => !isTerminal(i.status));
      const worst = [...svc.incidents].sort((a, b) => severityRank(b.severity) - severityRank(a.severity))[0];
      const lastSeen = [...svc.incidents].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt))[0];

      return {
        ...svc,
        activeCount: active.length,
        criticalCount: active.filter((i) => i.severity === 'Critical').length,
        health: active.length === 0 ? 'Healthy' : worst?.severity === 'Critical' ? 'Critical' : 'Degraded',
        worstActive: active[0] || null,
        lastSeen
      };
    })
    .sort((a, b) => b.activeCount - a.activeCount || b.incidents.length - a.incidents.length);
}
