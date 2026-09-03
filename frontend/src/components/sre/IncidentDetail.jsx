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
import { getRuleLabel, getSignalLabel } from '../../lib/labels';
import { resolveSource, sourceMeta } from '../../lib/source';

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

  // SDK-source chip (redesign brief section 6): which SDK/agent this incident's telemetry
  // actually came from, shown next to severity/status the same way the source filter elsewhere
  // in this redesign uses resolveSource + sourceMeta's tint/icon.
  const source = resolveSource(incident);
  const { label: sourceLabel, tint: sourceTint, icon: SourceIcon } = sourceMeta[source];

  // Duplicate-field dedup for the meta strip below: Service, Component ("affectedComponent") and
  // Application frequently carry the identical string (a known real-world case, not just the
  // component-is-the-endpoint case the old comment called out), so showing all of them plus
  // Endpoint would repeat the same value up to four times. Endpoint always renders regardless
  // (it has its own fallback-to-Component display below and is the most precise field), so its
  // displayed value is seeded first here; whichever of Service, Component or Application (in that
  // priority order) first introduces a value not already shown gets to render, and anything after
  // it that repeats a value already shown is dropped. This subsumes the previous
  // Component-vs-Endpoint-only check as the special case where Service/Application don't collide.
  const endpointDisplayValue = incident.affectedEndpoint || incident.affectedComponent || null;
  const shownMetaValues = endpointDisplayValue ? [endpointDisplayValue] : [];
  const isDuplicateMetaValue = (value) => {
    if (!value) return false;
    if (shownMetaValues.includes(value)) return true;
    shownMetaValues.push(value);
    return false;
  };
  const showService = !isDuplicateMetaValue(incident.service);
  const showComponent = Boolean(incident.affectedComponent) && !isDuplicateMetaValue(incident.affectedComponent);
  const showApplication = !isDuplicateMetaValue(incident.application);

  return (
    <div className="incident-detail">
      <header className="incident-detail-header">
        <div className="incident-detail-title">
          <span className="incident-key-lg">{incident.incidentKey}</span>
          <h2>{incident.title}</h2>

          <div className="incident-detail-badges">
            <SeverityBadge severity={incident.severity} />
            <StatusBadge status={incident.status} />
            <span
              className="status-badge"
              style={{
                gap: 4,
                color: sourceTint,
                borderColor: sourceTint,
                background: `color-mix(in srgb, ${sourceTint} 14%, transparent)`
              }}
              title={sourceLabel}
            >
              <SourceIcon className="w-4 h-4" />
              {sourceLabel}
            </span>
            {incident.signalCount > 0 && (
              <span className="signal-count">{incident.signalCount} correlated signals</span>
            )}
          </div>
        </div>

        <dl className="incident-detail-meta">
          {showService && (
            <div>
              <dt>Service</dt>
              <dd>{incident.service}</dd>
            </div>
          )}
          {showApplication && (
            <div>
              <dt>Application</dt>
              <dd>{incident.application}</dd>
            </div>
          )}
          <div>
            <dt>Environment</dt>
            <dd>{incident.environment}</dd>
          </div>
          {/* When the component IS the endpoint (true for most of this demo's simpler incidents),
              or matches Service/Application, showing it again would just print the same string
              under another label - see the dedup computed above the return statement. */}
          {showComponent && (
            <div>
              <dt>Component</dt>
              <dd>{incident.affectedComponent}</dd>
            </div>
          )}
          <div>
            <dt>Endpoint</dt>
            <dd>
              <code className="path-code" title={incident.affectedEndpoint || undefined}>{incident.affectedEndpoint || incident.affectedComponent || '--'}</code>
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

// Signals reported by the KAIRON Agent (a log tailer or process watcher, not the SDK) - see
// docs/OBSERVABILITY_MIGRATION.md. Distinguishing these in the table is the whole point of that
// migration made visible: proof this incident's evidence did not all come from one source.
const AGENT_SOURCED_METRICS = new Set(['logPattern', 'processCrash', 'processHighResource']);

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
                  <td title={signal.metric}>{getSignalLabel(signal.metric)}</td>
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
                    <code className="path-code" title={signal.rule}>{getRuleLabel(signal.rule)}</code>
                    {AGENT_SOURCED_METRICS.has(signal.metric) && (
                      <span className="agent-source-tag" title="Reported by the KAIRON Agent (log/process monitoring, not the SDK)">
                        Agent
                      </span>
                    )}
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
