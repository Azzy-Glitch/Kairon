import React, { useId, useState } from 'react';
import Button from '../ui/Button';
import { loadLastOperator, saveLastOperator } from '../../lib/operatorIdentity';
import { IncidentStatus, TERMINAL_STATUSES } from '../../types/incident';

/** States where something is already running: closing then would leave it unfinished. */
const IN_FLIGHT = [IncidentStatus.Remediating, IncidentStatus.Verifying];

export function canCloseIncident(incident) {
  return Boolean(incident) && !TERMINAL_STATUSES.includes(incident.status) && !IN_FLIGHT.includes(incident.status);
}

/**
 * Lets an operator close an incident that needs no further action - for example one diagnosed
 * before a remediation target was enabled, or a false alarm. Uses the backend's existing cancel
 * operation: any pending remediation is cancelled with it, nothing is executed, and the operator
 * and reason are recorded in the incident's audit trail.
 */
export default function CloseIncident({ incident, onClose, busy, error }) {
  const [open, setOpen] = useState(false);
  const [operator, setOperator] = useState(loadLastOperator);
  const [reason, setReason] = useState('');
  const uid = useId();

  if (!canCloseIncident(incident)) return null;

  const confirm = async () => {
    const name = operator.trim();
    if (!name) return;
    saveLastOperator(name);
    try {
      await onClose(name, reason.trim() || 'Closed by operator: no further action needed.');
      setOpen(false);
    } catch {
      // The error is shown below; the form stays open so the operator can retry.
    }
  };

  if (!open) {
    return (
      <div className="incident-close">
        <Button variant="ghost" size="compact" onClick={() => setOpen(true)}>Close incident</Button>
      </div>
    );
  }

  return (
    <div className="settings-delete-confirmation incident-close" role="group" aria-label="Close this incident">
      <p className="panel-pending-text">
        Closing marks this incident as Cancelled. Nothing is executed, any remediation still waiting for approval is
        cancelled with it, and your name and reason are recorded. If the problem comes back, KAIRON opens a new incident.
      </p>
      <label className="block-label" htmlFor={`${uid}-operator`}>Your name</label>
      <input id={`${uid}-operator`} className="approval-input" value={operator} onChange={(e) => setOperator(e.target.value)}
        autoComplete="off" placeholder="Recorded in the audit trail" />
      <label className="block-label" htmlFor={`${uid}-reason`}>Reason (optional)</label>
      <input id={`${uid}-reason`} className="approval-input" value={reason} onChange={(e) => setReason(e.target.value)}
        autoComplete="off" placeholder="For example: diagnosed before a remediation target was enabled" />
      {error && <p className="sdk-hint" role="alert">{error.message || 'Could not close the incident.'}</p>}
      <div className="settings-delete-confirmation-actions">
        <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Keep open</Button>
        <Button variant="danger" onClick={confirm} disabled={busy || !operator.trim()}>
          {busy ? 'Closing...' : 'Close incident'}
        </Button>
      </div>
    </div>
  );
}
