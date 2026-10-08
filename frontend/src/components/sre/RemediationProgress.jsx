import React from 'react';
import { RemediationBadge, VerificationBadge } from './Badges';
import { formatDateTime, percentChange } from '../../services/incidentService';
import { RemediationStatus, VerificationStatus } from '../../types/incident';
import { getActionLabel, getSignalLabel } from '../../lib/labels';
import { classifyExecutionError } from '../../lib/remediation';
import { IconZap } from '../Icons';

/**
 * Remediation progress (frontend PRD section 11): Approved -> Executing -> Verifying ->
 * Resolved | Failed. Approved-but-not-started and policy-refused actions are shown too, rather
 * than quietly absent, and success is only ever shown when the backend itself says so: the final
 * step reads "Resolved" only for a backend-Resolved incident.
 */
export function RemediationProgress({ actions, incidentStatus }) {
  if (!actions?.length) return null;

  return (
    <section className="panel remediation-panel">
      <div className="panel-header">
        <span className="panel-icon">
          <IconZap className="w-5 h-5" />
        </span>
        <h4>Remediation</h4>
      </div>

      <ul className="remediation-list">
        {actions.map((action) => (
          <RemediationItem key={action.id} action={action} incidentStatus={incidentStatus} />
        ))}
      </ul>
    </section>
  );
}

function RemediationItem({ action, incidentStatus }) {
  const status = action.status;
  const policyRejected = status === RemediationStatus.PolicyRejected;
  const failed = status === RemediationStatus.Failed;
  const executing = status === RemediationStatus.Executing;
  const executed = status === RemediationStatus.Executed;
  const verification = action.verificationResult;
  const verificationBad =
    verification?.status === VerificationStatus.Failed || verification?.status === VerificationStatus.Inconclusive;
  const resolved = executed && incidentStatus === 'Resolved';
  const finalFailed = failed || policyRejected || verificationBad || (incidentStatus === 'Failed' && !resolved);
  const failure = classifyExecutionError(action.executionError);

  return (
    <li className={`remediation-item remediation-${status.toLowerCase()}`}>
      <div className="remediation-head">
        <code className="recommendation-action" title={action.actionType}>{getActionLabel(action.actionType)}</code>
        <RemediationBadge status={status} />
        <span className="remediation-key">{action.actionKey}</span>
      </div>

      {action.targetWindowsServiceName && (
        <p className="remediation-line">
          Windows service <code className="path-code">{action.targetWindowsServiceName}</code>
          {action.targetHostName ? <> on <strong>{action.targetHostName}</strong></> : null}
        </p>
      )}

      <ol className="remediation-steps">
        <Step
          label={policyRejected ? 'Blocked by policy' : 'Approved'}
          done={Boolean(action.approvedAt) && !policyRejected}
          failed={policyRejected}
          at={action.approvedAt}
        />
        <Step
          label="Executing"
          done={Boolean(action.startedAt) && !executing && !failed}
          active={executing || (status === RemediationStatus.Approved && !action.startedAt)}
          failed={failed}
          at={action.startedAt}
        />
        <Step
          label="Verifying"
          done={Boolean(verification?.completedAt) && !verificationBad}
          active={executed && incidentStatus === 'Verifying'}
          failed={verificationBad}
          at={verification?.startedAt}
        />
        <Step
          label={finalFailed ? 'Failed' : 'Resolved'}
          done={resolved}
          failed={finalFailed}
          at={resolved ? verification?.completedAt || action.completedAt : null}
        />
      </ol>

      {status === RemediationStatus.Approved && !action.startedAt && (
        <p className="remediation-line">Approved - waiting to start.</p>
      )}

      {action.approvedBy && (
        <p className="remediation-line">
          Approved by <strong>{action.approvedBy}</strong> at {formatDateTime(action.approvedAt)}
        </p>
      )}

      {action.rejectedBy && (
        <p className="remediation-line">
          Rejected by <strong>{action.rejectedBy}</strong>
          {action.rejectionReason ? `: ${action.rejectionReason}` : ''}
        </p>
      )}

      {policyRejected && (
        <p className="remediation-error">
          Blocked by policy: {action.policyDecision || 'remediation policy did not allow this action.'}
        </p>
      )}

      {action.executionResult && <p className="remediation-result">{action.executionResult}</p>}

      {/* Failures are stated plainly, in words an operator can act on; the raw backend error stays
          available underneath for whoever has to fix it. */}
      {failure && (
        <>
          <p className="remediation-error">
            Failed: {failure.headline || failure.detail}
            {failure.headline && failure.detail ? <span className="remediation-error-detail"> - {failure.detail}</span> : null}
          </p>
          {failure.headline && (
            <details className="remediation-error-raw">
              <summary>Technical details</summary>
              <code className="path-code">{failure.raw}</code>
            </details>
          )}
        </>
      )}
    </li>
  );
}

function Step({ label, done, active, failed, at }) {
  const state = failed ? 'failed' : done ? 'done' : active ? 'active' : 'pending';

  return (
    <li className={`remediation-step step-${state}`}>
      <span className="step-dot" />
      <span className="step-label">{label}</span>
      {at && <span className="step-time">{formatDateTime(at)}</span>}
    </li>
  );
}

/**
 * Verification panel (frontend PRD section 12). The before/after table is the point: it is what
 * visually proves the remediation worked, or did not.
 */
export function VerificationPanel({ verification }) {
  if (!verification) return null;

  const comparisons = verification.comparisons || [];

  return (
    <section className="panel verification-panel">
      <div className="panel-header">
        <span className="panel-icon">
          <IconZap className="w-5 h-5" />
        </span>
        <h4>Verification</h4>
        <VerificationBadge status={verification.status} />
      </div>

      <p className="verification-summary">{verification.summary}</p>

      {verification.status === VerificationStatus.Inconclusive && (
        <p className="verification-warning">
          No fresh telemetry arrived after remediation, so recovery could not be confirmed.
        </p>
      )}

      {comparisons.length > 0 ? (
        <div className="table-responsive">
          <table className="custom-table verification-table">
            <thead>
              <tr>
                <th>Metric</th>
                <th>Before</th>
                <th>After</th>
                <th>Change</th>
                <th>Threshold</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {comparisons.map((c) => {
                const change = percentChange(c.before, c.after);

                // A metric the service stopped reporting is unknown, not breaching. Rendering it
                // as a failure would misreport a successful remediation - which is exactly what
                // happened the first time this table met a service that stopped emitting retries.
                const unknown = c.after === null || c.after === undefined;

                const rowClass = unknown ? 'row-unknown' : c.meetsThreshold ? 'row-good' : 'row-bad';

                return (
                  <tr key={c.metric} className={rowClass}>
                    <td title={c.metric}>{getSignalLabel(c.metric)}</td>
                    <td>{format(c.before, c.unit)}</td>
                    <td>
                      <strong>{format(c.after, c.unit)}</strong>
                    </td>
                    <td className={change === null ? '' : change < 0 ? 'change-good' : 'change-bad'}>
                      {change === null ? '--' : `${change > 0 ? '+' : ''}${change}%`}
                    </td>
                    <td>{format(c.threshold, c.unit)}</td>
                    <td>
                      {unknown
                        ? 'Not reported'
                        : c.meetsThreshold
                          ? 'Within threshold'
                          : 'Still breaching'}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="panel-pending-text">No comparable metrics were available for this verification.</p>
      )}

      <div className="verification-score">
        Recovery score: <strong>{Math.round((verification.recoveryScore || 0) * 100)}%</strong> of
        affected metrics returned within threshold
      </div>
    </section>
  );
}

function format(value, unit) {
  if (value === null || value === undefined) return '--';
  const rounded = Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
  return `${rounded}${unit || ''}`;
}
