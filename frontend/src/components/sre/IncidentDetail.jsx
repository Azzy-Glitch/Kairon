import React, { useMemo } from 'react';
import { SeverityBadge, StatusBadge } from './Badges';
import { AiInvestigationPanel, PredictionPanel, RecommendationPanel } from './AiPanels';
import ApprovalPanel from './ApprovalPanel';
import { RemediationProgress, VerificationPanel } from './RemediationProgress';
import { IncidentTimeline, LifecycleRail } from './IncidentTimeline';
import { AsyncView } from './StateViews';
import {
  buildLifecycle,
  executedActions,
  formatDateTime,
  isResolvedByBackend,
  latestVerification,
  pendingAction,
  verificationFailed
} from '../../services/incidentService';
import { IncidentStatus } from '../../types/incident';

/**
 * Incident detail (frontend PRD section 6).
 *
 * Section order follows the operator's questions: what happened, why, what will happen, what
 * should be done, what needs approving, what ran, and did it work.
 */
export default function IncidentDetail({ query, actions }) {
  return (
    <AsyncView
      query={query}
      loadingLabel="Loading incident..."
      emptyTitle="Select an incident"
      emptyHint="Choose an incident from the feed to see its full investigation."
    >
      {(incident) => <DetailBody incident={incident} actions={actions} />}
    </AsyncView>
  );
}

function DetailBody({ incident, actions }) {
  const lifecycle = useMemo(() => buildLifecycle(incident), [incident]);
  const awaiting = pendingAction(incident);
  const executed = executedActions(incident);
  const verification = latestVerification(incident);

  const resolved = isResolvedByBackend(incident);
  const failedVerification = verificationFailed(incident);

  return (
    <div className="incident-detail">
      <header className="incident-detail-header">
        <div className="incident-detail-title">
          <span className="incident-key-lg">{incident.incidentKey}</span>
          <h2>{incident.title}</h2>

          <div className="incident-detail-badges">
            <SeverityBadge severity={incident.severity} />
            <StatusBadge status={incident.status} />
            {incident.signalCount > 0 && (
              <span className="signal-count">{incident.signalCount} correlated signals</span>
            )}
          </div>
        </div>

        <dl className="incident-detail-meta">
          <div>
            <dt>Service</dt>
            <dd>{incident.service}</dd>
          </div>
          <div>
            <dt>Application</dt>
            <dd>{incident.application}</dd>
          </div>
          <div>
            <dt>Environment</dt>
            <dd>{incident.environment}</dd>
          </div>
          <div>
            <dt>Component</dt>
            <dd>{incident.affectedComponent || '--'}</dd>
          </div>
          <div>
            <dt>Endpoint</dt>
            <dd>
              <code className="path-code">{incident.affectedEndpoint || '--'}</code>
            </dd>
          </div>
          <div>
            <dt>Detected</dt>
            <dd>{formatDateTime(incident.detectedAt)}</dd>
          </div>
        </dl>
      </header>

      <LifecycleRail stages={lifecycle} />

      {/* Only the backend can declare an incident resolved (frontend PRD section 13). This banner
          is bound to its status, never to how the metrics look. */}
      {resolved && (
        <div className="resolution-banner resolution-good">
          <strong>Resolved</strong>
          <span>
            Backend verification confirmed recovery at {formatDateTime(incident.resolvedAt)}.
          </span>
        </div>
      )}

      {failedVerification && !resolved && (
        <div className="resolution-banner resolution-bad">
          <strong>Verification failed</strong>
          <span>{verification?.summary || 'Recovery could not be confirmed.'}</span>
        </div>
      )}

      {incident.status === IncidentStatus.Failed && (
        <div className="resolution-banner resolution-bad">
          <strong>Failed</strong>
          <span>{incident.failureReason || 'This incident could not be remediated automatically.'}</span>
        </div>
      )}

      {incident.status === IncidentStatus.Rejected && (
        <div className="resolution-banner resolution-neutral">
          <strong>Rejected</strong>
          <span>{incident.failureReason || 'An operator rejected the proposed remediation.'}</span>
        </div>
      )}

      {incident.status === IncidentStatus.Cancelled && (
        <div className="resolution-banner resolution-neutral">
          <strong>Cancelled</strong>
          <span>{incident.failureReason || 'This incident was cancelled by an operator.'}</span>
        </div>
      )}

      <div className="incident-detail-grid">
        <div className="incident-detail-main">
          <SymptomsPanel incident={incident} />

          <AiInvestigationPanel
            diagnosis={incident.diagnosis}
            failureReason={incident.diagnosis ? null : incident.failureReason}
            stale={incident.diagnosisStale}
            // Re-investigation is offered while the incident is still open and nothing has been
            // approved. Once a remediation is in flight the backend refuses it anyway, so the
            // affordance is hidden rather than shown and then rejected.
            onRetry={
              canReinvestigate(incident) ? () => actions.investigate(incident.id) : null
            }
            retrying={actions.busyActionId === 'investigate'}
          />

          <PredictionPanel prediction={incident.prediction} />

          <RecommendationPanel recommendations={incident.recommendations} />

          {awaiting && (
            <ApprovalPanel
              action={awaiting}
              busy={actions.busyActionId === awaiting.id}
              error={actions.actionError}
              onApprove={(actionId, operator, note) =>
                actions.approve(incident.id, actionId, operator, note)
              }
              onReject={(actionId, operator, reason) =>
                actions.reject(incident.id, actionId, operator, reason)
              }
            />
          )}

          {executed.length > 0 && (
            <RemediationProgress actions={executed} incidentStatus={incident.status} />
          )}

          <VerificationPanel verification={verification} />
        </div>

        <aside className="incident-detail-side">
          <IncidentTimeline events={incident.timeline} />
        </aside>
      </div>
    </div>
  );
}

/**
 * Whether the operator may ask for a fresh investigation.
 *
 * Mirrors the backend's own guard: open incident, and no remediation approved or executed. Getting
 * this wrong is harmless (the backend refuses), but showing a button that always fails is not.
 */
function canReinvestigate(incident) {
  const openForInvestigation = [
    IncidentStatus.Detected,
    IncidentStatus.Investigating,
    IncidentStatus.Diagnosed,
    IncidentStatus.Predicted,
    IncidentStatus.RecommendationReady,
    IncidentStatus.AwaitingApproval
  ].includes(incident.status);

  if (!openForInvestigation) return false;

  return !(incident.actions || []).some((a) =>
    ['Approved', 'Executing', 'Executed'].includes(a.status)
  );
}

/**
 * Symptoms (frontend PRD section 6): the measured signals that created the incident. Kept visually
 * separate from the AI panels, because this is telemetry rather than inference.
 */
function SymptomsPanel({ incident }) {
  const signals = incident.correlatedSignals || [];

  return (
    <section className="panel symptoms-panel">
      <div className="panel-header">
        <h4>Symptoms</h4>
        <span className="telemetry-badge">Measured telemetry</span>
      </div>

      {signals.length > 0 ? (
        <div className="table-responsive">
          <table className="custom-table">
            <thead>
              <tr>
                <th>Signal</th>
                <th>Observed</th>
                <th>Threshold</th>
                <th>Severity</th>
                <th>Rule</th>
              </tr>
            </thead>
            <tbody>
              {signals.map((signal, index) => (
                <tr key={index}>
                  <td>{signal.metric}</td>
                  <td>
                    <strong>
                      {signal.observed}
                      {signal.unit}
                    </strong>
                  </td>
                  <td>
                    {signal.threshold}
                    {signal.unit}
                  </td>
                  <td>
                    <SeverityBadge severity={signal.severity} size="sm" />
                  </td>
                  <td>
                    <code className="path-code">{signal.rule}</code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <ul className="ai-list">
          {(incident.symptoms || []).map((symptom, index) => (
            <li key={index}>{symptom}</li>
          ))}
        </ul>
      )}

      {incident.telemetryReferences?.length > 0 && (
        <p className="symptoms-footnote">
          {incident.telemetryReferences.length} supporting telemetry records linked to this incident.
        </p>
      )}
    </section>
  );
}
