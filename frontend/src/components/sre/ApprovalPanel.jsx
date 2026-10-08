import React, { useState } from 'react';
import { RiskBadge } from './Badges';
import { getActionLabel } from '../../lib/labels';
import { describeReadiness } from '../../lib/remediation';
import { IconShield } from '../Icons';

/**
 * The approval gate (frontend PRD section 10).
 *
 * Approval has to be deliberate, so this panel asks for three things before it will enable the
 * approve button: an operator identity, and an explicit confirmation step. There is no path here
 * that approves anything automatically, and no default operator name.
 */
export default function ApprovalPanel({ action, environment, onApprove, onReject, busy, error }) {
  const [operator, setOperator] = useState('');
  const [note, setNote] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [mode, setMode] = useState(null);
  // A rejected approve/reject call must never become an unhandled promise rejection. The caller
  // normally surfaces the failure through `error`; this is the fallback when it doesn't.
  const [localError, setLocalError] = useState(null);

  if (!action) return null;

  const identityMissing = operator.trim().length === 0;

  const beginApprove = () => {
    setMode('approve');
    setConfirming(true);
  };

  const beginReject = () => {
    setMode('reject');
    setConfirming(true);
  };

  const cancel = () => {
    setConfirming(false);
    setMode(null);
  };

  const confirm = async () => {
    if (identityMissing) return;
    setLocalError(null);

    try {
      if (mode === 'approve') {
        await onApprove(action.id, operator.trim(), note.trim() || null);
      } else {
        await onReject(action.id, operator.trim(), note.trim() || null);
      }
      setNote('');
    } catch (err) {
      setLocalError(err || { message: 'The request failed.' });
    } finally {
      setConfirming(false);
      setMode(null);
    }
  };

  const actionLabel = getActionLabel(action.actionType);
  const serviceName = action.targetWindowsServiceName;
  const hostName = action.targetHostName;
  const readiness = action.targetReadiness ? describeReadiness(action.targetReadiness) : null;
  const ready = action.targetReadiness === 'Ready';
  const shownError = error || localError;

  // Plain statement of exactly what will happen - no invented environment wording.
  const approveQuestion = serviceName
    ? `Execute "${actionLabel}" on the Windows service "${serviceName}"${hostName ? ` on ${hostName}` : ''}${environment ? ` (${environment})` : ''}?`
    : `Execute "${actionLabel}" now?`;

  return (
    <section className="panel approval-panel">
      <div className="panel-header">
        <span className="panel-icon">
          <IconShield className="w-5 h-5" />
        </span>
        <h4>Operator Approval Required</h4>
        <span className="approval-flag">Awaiting decision</span>
      </div>

      <div className="approval-action-card">
        <div className="approval-action-head">
          <code className="recommendation-action" title={action.actionType}>{actionLabel}</code>
          <RiskBadge risk={action.riskLevel} />
        </div>

        <dl className="approval-details">
          {(serviceName || action.targetReadiness) && (
            <>
              <div>
                <dt>Windows service</dt>
                <dd>{serviceName ? <code className="path-code">{serviceName}</code> : 'No remediation target configured.'}</dd>
              </div>
              <div>
                <dt>Machine</dt>
                <dd>{hostName || 'Unknown'}</dd>
              </div>
            </>
          )}
          {environment && (
            <div>
              <dt>Environment</dt>
              <dd>{environment}</dd>
            </div>
          )}
          <div>
            <dt>Reason</dt>
            <dd>{action.reason || 'Not provided.'}</dd>
          </div>
          <div>
            <dt>Expected effect</dt>
            <dd>{action.expectedOutcome || 'Not provided.'}</dd>
          </div>
          <div>
            <dt>Risk</dt>
            <dd>{action.riskLevel || 'Unknown'}</dd>
          </div>
          {readiness && (
            <div>
              <dt>Target readiness</dt>
              <dd className={ready ? 'approval-readiness-ok' : 'approval-readiness-problem'}>
                {ready ? '✓ Ready' : `✗ ${readiness.label}`}
                {!ready && readiness.explanation && <span className="approval-readiness-detail"> - {readiness.explanation}</span>}
              </dd>
            </div>
          )}
          <div>
            <dt>Policy</dt>
            <dd>{action.policyDecision || 'Validated against remediation policy.'}</dd>
          </div>
        </dl>
      </div>

      <div className="approval-form">
        <label className="block-label" htmlFor="approval-operator">
          Operator identity (recorded in the audit trail)
        </label>
        <input
          id="approval-operator"
          className="approval-input"
          value={operator}
          onChange={(e) => setOperator(e.target.value)}
          placeholder="your name"
          autoComplete="off"
        />

        <label className="block-label" htmlFor="approval-note">
          Note (optional)
        </label>
        <input
          id="approval-note"
          className="approval-input"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder="why you are approving or rejecting"
          autoComplete="off"
        />
      </div>

      {shownError && <p className="approval-error" role="alert">{shownError.message || 'The request failed.'}</p>}

      {!confirming ? (
        <div className="approval-buttons">
          <button
            type="button"
            className="approve-btn"
            onClick={beginApprove}
            disabled={identityMissing || Boolean(busy)}
            title={identityMissing ? 'Enter your operator identity first' : undefined}
          >
            Approve and run
          </button>

          <button
            type="button"
            className="reject-btn ghost"
            onClick={beginReject}
            disabled={identityMissing || Boolean(busy)}
            title={identityMissing ? 'Enter your operator identity first' : undefined}
          >
            Reject
          </button>
        </div>
      ) : (
        // A second, explicit step. Executing a remediation is not something a stray click should
        // be able to do.
        <div className="approval-confirm">
          <p>
            {mode === 'approve' ? approveQuestion : `Reject "${actionLabel}"?`}
          </p>
          <div className="approval-buttons">
            <button
              type="button"
              className={mode === 'approve' ? 'approve-btn' : 'reject-btn'}
              onClick={confirm}
              disabled={Boolean(busy)}
            >
              {busy ? 'Working...' : mode === 'approve' ? 'Yes, execute it' : 'Yes, reject it'}
            </button>
            <button type="button" className="secondary-btn" onClick={cancel} disabled={Boolean(busy)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
