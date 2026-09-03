import React, { useState } from 'react';
import { RiskBadge } from './Badges';
import { getActionLabel } from '../../lib/labels';
import { IconShield } from '../Icons';

/**
 * The approval gate (frontend PRD section 10).
 *
 * Approval has to be deliberate, so this panel asks for three things before it will enable the
 * approve button: an operator identity, and an explicit confirmation step. There is no path here
 * that approves anything automatically, and no default operator name.
 */
export default function ApprovalPanel({ action, onApprove, onReject, busy, error }) {
  const [operator, setOperator] = useState('');
  const [note, setNote] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [mode, setMode] = useState(null);

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

    if (mode === 'approve') {
      await onApprove(action.id, operator.trim(), note.trim() || null);
    } else {
      await onReject(action.id, operator.trim(), note.trim() || null);
    }

    setConfirming(false);
    setMode(null);
    setNote('');
  };

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
          <code className="recommendation-action" title={action.actionType}>{getActionLabel(action.actionType)}</code>
          <RiskBadge risk={action.riskLevel} />
        </div>

        <dl className="approval-details">
          <div>
            <dt>Reason</dt>
            <dd>{action.reason || 'Not provided.'}</dd>
          </div>
          <div>
            <dt>Expected outcome</dt>
            <dd>{action.expectedOutcome || 'Not provided.'}</dd>
          </div>
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

      {error && <p className="approval-error">{error.message}</p>}

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
            {mode === 'approve'
              ? `Execute "${getActionLabel(action.actionType)}" against the ${action.riskLevel?.toLowerCase() || ''} risk demo environment?`
              : `Reject "${getActionLabel(action.actionType)}"?`}
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
