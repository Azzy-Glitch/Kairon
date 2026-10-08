import React, { useId, useState } from 'react';
import { RiskBadge } from './Badges';
import { getActionLabel } from '../../lib/labels';
import { describeReadiness } from '../../lib/remediation';
import { describeActionTarget, loadLastOperator, saveLastOperator } from '../../lib/operatorIdentity';
import { IconShield } from '../Icons';

/**
 * The approval gate (frontend PRD section 10).
 *
 * Approval has to be deliberate, so this panel asks for an operator identity, an explicit choice of
 * which pending action to run (when the incident has more than one), and an explicit confirmation
 * step. There is no path here that approves anything automatically. The last-used operator name may
 * be pre-filled as a typing convenience only - it is never treated as authorization, and the
 * backend records and revalidates every decision on its own.
 *
 * Accepts either `actions` (every AwaitingApproval action for the incident) or a single `action`.
 */
export default function ApprovalPanel({ action, actions, environment, onApprove, onReject, busy, error }) {
  const pending = actions ? actions.filter(Boolean) : action ? [action] : [];

  const [operator, setOperator] = useState(loadLastOperator);
  const [note, setNote] = useState('');
  const [chosenId, setChosenId] = useState(null);
  const [confirming, setConfirming] = useState(false);
  const [mode, setMode] = useState(null);
  // A rejected approve/reject call must never become an unhandled promise rejection. The caller
  // normally surfaces the failure through `error`; this is the fallback when it doesn't.
  const [localError, setLocalError] = useState(null);

  const uid = useId();
  const operatorHelpId = `${uid}-operator-help`;
  const disabledReasonId = `${uid}-disabled-reason`;
  const alternativesNoteId = `${uid}-alternatives`;

  if (pending.length === 0) return null;

  const multiple = pending.length > 1;
  // No default choice when there are alternatives: the operator must pick one. A single pending
  // action is the only one that can be meant, so it is selected for them.
  const selected = multiple ? pending.find((a) => a.id === chosenId) || null : pending[0];
  const others = pending.length - 1;

  const identityMissing = operator.trim().length === 0;
  const selectionMissing = !selected;

  const disabledReasons = [];
  if (selectionMissing) disabledReasons.push('Choose which action to approve or reject.');
  if (identityMissing) disabledReasons.push('Enter your name to approve or reject.');
  const disabledReason = disabledReasons.join(' ');
  const decisionBlocked = disabledReasons.length > 0;

  const begin = (nextMode) => {
    if (decisionBlocked) return;
    setMode(nextMode);
    setConfirming(true);
  };

  const cancel = () => {
    setConfirming(false);
    setMode(null);
  };

  const confirm = async () => {
    if (decisionBlocked) return;
    setLocalError(null);
    const name = operator.trim();

    try {
      if (mode === 'approve') {
        await onApprove(selected.id, name, note.trim() || null);
      } else {
        await onReject(selected.id, name, note.trim() || null);
      }
      saveLastOperator(name);
      setNote('');
    } catch (err) {
      setLocalError(err || { message: 'The request failed.' });
    } finally {
      setConfirming(false);
      setMode(null);
    }
  };

  const shownError = error || localError;
  const selectedLabel = selected ? getActionLabel(selected.actionType) : '';

  // Plain statement of exactly what will happen - no invented environment wording.
  const approveQuestion = selected
    ? selected.targetWindowsServiceName
      ? `Execute "${selectedLabel}" on the Windows service "${selected.targetWindowsServiceName}"${selected.targetHostName ? ` on ${selected.targetHostName}` : ''}${environment ? ` (${environment})` : ''}?`
      : `Execute "${selectedLabel}" now?`
    : '';
  const rejectQuestion = selected ? `Reject "${selectedLabel}"${describeActionTarget(selected, environment)}?` : '';

  return (
    <section className="panel approval-panel">
      <div className="panel-header">
        <span className="panel-icon">
          <IconShield className="w-5 h-5" />
        </span>
        <h4>Operator Approval Required</h4>
        <span className="approval-flag">Awaiting decision</span>
      </div>

      {multiple ? (
        <fieldset className="approval-choice" aria-describedby={alternativesNoteId}>
          <legend className="block-label">
            Choose the action to approve ({pending.length} pending alternatives)
          </legend>
          <p id={alternativesNoteId} className="approval-help approval-alternatives-note">
            Approving one of these cancels the other {others} pending alternative{others === 1 ? '' : 's'} for this
            incident. Reject applies only to the action you choose.
          </p>
          <div className="approval-choice-list">
            {pending.map((a) => {
              const isChosen = selected?.id === a.id;
              return (
                <label key={a.id} className={`approval-choice-option${isChosen ? ' is-selected' : ''}`}>
                  <input
                    type="radio"
                    name={`${uid}-pending-action`}
                    value={a.id}
                    checked={isChosen}
                    onChange={() => setChosenId(a.id)}
                    disabled={Boolean(busy) || confirming}
                    aria-label={`${getActionLabel(a.actionType)}${describeActionTarget(a, environment)}`}
                  />
                  <ActionCard action={a} environment={environment} />
                </label>
              );
            })}
          </div>
        </fieldset>
      ) : (
        <ActionCard action={pending[0]} environment={environment} />
      )}

      <div className="approval-form">
        <label className="block-label" htmlFor={`${uid}-operator`}>
          Your name (required — recorded in the audit trail)
        </label>
        <input
          id={`${uid}-operator`}
          className="approval-input"
          value={operator}
          onChange={(e) => setOperator(e.target.value)}
          placeholder="e.g. Jane Smith"
          autoComplete="off"
          required
          aria-required="true"
          aria-describedby={operatorHelpId}
        />
        <p id={operatorHelpId} className="approval-help">
          Approving or rejecting requires your name. It is stored with the decision so the audit trail shows who
          made it.
        </p>

        <label className="block-label" htmlFor={`${uid}-note`}>
          Note (optional)
        </label>
        <input
          id={`${uid}-note`}
          className="approval-input"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder="why you are approving or rejecting"
          autoComplete="off"
        />
      </div>

      {shownError && <p className="approval-error" role="alert">{shownError.message || 'The request failed.'}</p>}

      {!confirming ? (
        <>
          <div className="approval-buttons">
            <button
              type="button"
              className="approve-btn"
              onClick={() => begin('approve')}
              disabled={decisionBlocked || Boolean(busy)}
              aria-describedby={decisionBlocked ? disabledReasonId : undefined}
            >
              Approve and run
            </button>

            <button
              type="button"
              className="reject-btn ghost"
              onClick={() => begin('reject')}
              disabled={decisionBlocked || Boolean(busy)}
              aria-describedby={decisionBlocked ? disabledReasonId : undefined}
            >
              Reject
            </button>
          </div>
          {decisionBlocked && (
            <p id={disabledReasonId} className="approval-disabled-reason" aria-live="polite">
              {disabledReason}
            </p>
          )}
        </>
      ) : (
        // A second, explicit step. Executing a remediation is not something a stray click should
        // be able to do.
        <div className="approval-confirm">
          <p>{mode === 'approve' ? approveQuestion : rejectQuestion}</p>
          {mode === 'approve' && multiple && (
            <p className="approval-confirm-note">
              This cancels the other {others} pending alternative{others === 1 ? '' : 's'} for this incident.
            </p>
          )}
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

/** Everything the operator needs to know about one pending action before deciding. */
function ActionCard({ action, environment }) {
  const actionLabel = getActionLabel(action.actionType);
  const serviceName = action.targetWindowsServiceName;
  const hostName = action.targetHostName;
  const readiness = action.targetReadiness ? describeReadiness(action.targetReadiness) : null;
  const ready = action.targetReadiness === 'Ready';

  return (
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
  );
}
