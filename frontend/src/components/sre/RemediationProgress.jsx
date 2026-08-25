import React from 'react';
import { RemediationBadge, VerificationBadge } from './Badges';
import { formatDateTime, percentChange } from '../../services/incidentService';
import { RemediationStatus, VerificationStatus } from '../../types/incident';
import { IconZap } from '../Icons';

/**
 * Remediation progress (frontend PRD section 11): Approved -> Executing -> Executed -> Verifying
 * -> Verified, with failures clearly represented rather than quietly absent.
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
          <li key={action.id} className={`remediation-item remediation-${action.status.toLowerCase()}`}>
            <div className="remediation-head">
              <code className="recommendation-action">{action.actionType}</code>
              <RemediationBadge status={action.status} />
              <span className="remediation-key">{action.actionKey}</span>
            </div>

            <ol className="remediation-steps">
              <Step label="Approved" done={Boolean(action.approvedAt)} at={action.approvedAt} />
              <Step
                label="Executing"
                done={Boolean(action.startedAt)}
                active={action.status === RemediationStatus.Executing}
                at={action.startedAt}
              />
              <Step
                label="Executed"
                done={action.status === RemediationStatus.Executed}
                failed={action.status === RemediationStatus.Failed}
                at={action.completedAt}
              />
              <Step
                label="Verifying"
                done={Boolean(action.verificationResult)}
                active={incidentStatus === 'Verifying'}
                at={action.verificationResult?.startedAt}
              />
              <Step
                label="Verified"
                done={action.verificationResult?.status === VerificationStatus.Passed}
                failed={
                  action.verificationResult?.status === VerificationStatus.Failed ||
                  action.verificationResult?.status === VerificationStatus.Inconclusive
                }
                at={action.verificationResult?.completedAt}
              />
            </ol>

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

            {action.executionResult && <p className="remediation-result">{action.executionResult}</p>}

            {/* Failures are stated plainly. A remediation that did not work is the thing the
                operator most needs to see. */}
            {action.executionError && (
              <p className="remediation-error">Failed: {action.executionError}</p>
            )}
          </li>
        ))}
      </ul>
    </section>
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
                    <td>{c.metric}</td>
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
