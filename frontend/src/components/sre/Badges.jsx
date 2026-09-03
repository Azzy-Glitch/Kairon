import React from 'react';
import { IncidentStatus, RemediationStatus, VerificationStatus } from '../../types/incident';
import { getPhaseLabel } from '../../lib/labels';

/** Severity must be visually obvious in the feed (frontend PRD section 5). */
export function SeverityBadge({ severity, size = 'md' }) {
  const key = (severity || 'unknown').toLowerCase();
  return <span className={`sev-badge sev-${key} sev-${size}`}>{severity || 'Unknown'}</span>;
}

/** Lifecycle status, colour-coded by what the operator has to do about it. */
export function StatusBadge({ status }) {
  return <span className={`status-badge status-${statusTone(status)}`}>{humanize(status)}</span>;
}

export function RiskBadge({ risk }) {
  const key = (risk || 'unknown').toLowerCase();
  return <span className={`risk-badge risk-${key}`}>{risk || 'Unknown'} risk</span>;
}

export function RemediationBadge({ status }) {
  const tone =
    status === RemediationStatus.Executed
      ? 'good'
      : status === RemediationStatus.Failed || status === RemediationStatus.PolicyRejected
        ? 'bad'
        : status === RemediationStatus.AwaitingApproval
          ? 'attention'
          : 'neutral';

  return <span className={`status-badge status-${tone}`}>{humanize(status)}</span>;
}

export function VerificationBadge({ status }) {
  const tone =
    status === VerificationStatus.Passed
      ? 'good'
      : status === VerificationStatus.Failed
        ? 'bad'
        : status === VerificationStatus.Inconclusive
          ? 'attention'
          : 'neutral';

  return <span className={`status-badge status-${tone}`}>{humanize(status)}</span>;
}

/**
 * Marks content the AI produced, so it is never mistaken for measured telemetry
 * (frontend PRD section 7).
 */
export function AiBadge({ label = 'AI estimate' }) {
  return <span className="ai-badge">{label}</span>;
}

function statusTone(status) {
  switch (status) {
    case IncidentStatus.Resolved:
      return 'good';
    case IncidentStatus.Failed:
    case IncidentStatus.Rejected:
      return 'bad';
    case IncidentStatus.Cancelled:
      return 'neutral';
    case IncidentStatus.AwaitingApproval:
      return 'attention';
    case IncidentStatus.Remediating:
    case IncidentStatus.Verifying:
      return 'active';
    default:
      return 'info';
  }
}

/**
 * "RecommendationReady" reads badly on a badge. Tries the curated copy layer first (real sentence
 * case, e.g. "Awaiting approval"); only for a value that layer doesn't know about does this fall
 * back to a generic camelCase split, still forced to sentence case rather than Title Case.
 */
export function humanize(value) {
  if (!value) return 'Unknown';

  const known = getPhaseLabel(value);
  if (known !== value) return known;

  const spaced = String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}
