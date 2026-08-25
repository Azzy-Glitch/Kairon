import React, { useMemo, useState } from 'react';
import IncidentDetail from './IncidentDetail';
import { SeverityBadge, StatusBadge } from './Badges';
import { ErrorState } from './StateViews';
import { useDemo } from '../../hooks/useDemo';
import { useIncident, useIncidentActions, useIncidents } from '../../hooks/useIncidents';
import { formatMetricValue, relativeTime } from '../../services/incidentService';
import { IncidentStatus } from '../../types/incident';
import { IconZap, IconServer } from '../Icons';

/**
 * The competition demo view (frontend PRD sections 14, 15 and 26).
 *
 * One button starts the controlled order-processing retry-loop scenario. Everything after that is
 * the real pipeline: real telemetry, real detection, real AI investigation, real approval, real
 * remediation, real verification. Nothing on this screen is scripted, which is the point - a judge
 * is watching the system work, not an animation of it working.
 */

/** The pipeline stages, in the order the PRD asks a judge to observe them. */
const DEMO_STAGES = [
  { key: 'normal', label: 'NORMAL' },
  { key: 'failure', label: 'FAILURE' },
  { key: 'detected', label: 'DETECTED' },
  { key: 'investigating', label: 'INVESTIGATING' },
  { key: 'rootcause', label: 'ROOT CAUSE' },
  { key: 'prediction', label: 'PREDICTION' },
  { key: 'recommendation', label: 'RECOMMENDATION' },
  { key: 'approval', label: 'APPROVAL' },
  { key: 'remediation', label: 'REMEDIATION' },
  { key: 'verification', label: 'VERIFICATION' },
  { key: 'resolved', label: 'RESOLVED' }
];

export default function DemoRunner() {
  const demo = useDemo();
  const feed = useIncidents({ status: 'active', pollMs: 3000 });
  const [pinnedId, setPinnedId] = useState(null);

  // Follow the newest demo incident automatically, so the operator never has to hunt for it.
  const demoIncident = useMemo(
    () => feed.incidents.find((i) => i.environment === 'Demo') || feed.incidents[0] || null,
    [feed.incidents]
  );

  const trackedId = pinnedId || demoIncident?.id || null;
  const detail = useIncident(trackedId, { pollMs: 2500 });

  const actions = useIncidentActions(async () => {
    await Promise.all([detail.reload({ silent: true }), feed.reload({ silent: true })]);
  });

  const state = demo.data;
  const reachedStage = currentStage(state, detail.data);

  return (
    <div className="demo-runner animate-fade-in">
      <section className="section-card demo-control-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge">
              <IconZap className="w-6 h-6" />
            </div>
            <div>
              <h3>Incident Simulation</h3>
              <p className="section-desc">
                Runs the controlled order-processing retry-loop scenario. Detection, AI
                investigation, approval and remediation are all real - only the failing service is
                simulated, and only inside the demo environment.
              </p>
            </div>
          </div>

          <div className="flex-actions">
            <button
              type="button"
              className="primary-btn"
              onClick={demo.start}
              disabled={demo.busy === 'start' || state?.running}
            >
              {demo.busy === 'start' ? 'Starting...' : state?.running ? 'Running' : 'Run Incident Simulation'}
            </button>

            <button
              type="button"
              className="secondary-btn"
              onClick={demo.stop}
              disabled={demo.busy === 'stop' || !state?.running}
            >
              Reset
            </button>

            <button
              type="button"
              className="secondary-btn"
              onClick={demo.evaluate}
              disabled={demo.busy === 'evaluate'}
              title="Run a detection pass now instead of waiting for the next sweep"
            >
              Evaluate now
            </button>
          </div>
        </div>

        {demo.error && !demo.data && <ErrorState error={demo.error} onRetry={() => demo.reload()} />}

        <ol className="demo-pipeline">
          {DEMO_STAGES.map((stage, index) => (
            <li
              key={stage.key}
              className={`demo-stage ${index < reachedStage ? 'done' : index === reachedStage ? 'active' : 'pending'}`}
            >
              <span className="demo-stage-dot" />
              <span className="demo-stage-label">{stage.label}</span>
            </li>
          ))}
        </ol>
      </section>

      <section className="section-card demo-telemetry-card">
        <div className="panel-header">
          <span className="panel-icon">
            <IconServer className="w-5 h-5" />
          </span>
          <h4>Demo environment</h4>
          {state?.usingLocalSimulator && (
            <span className="telemetry-badge">In-process simulator (demo app not running)</span>
          )}
        </div>

        <div className="demo-metrics">
          <DemoMetric label="CPU" value={state?.cpuPercent} unit="%" threshold={80} />
          <DemoMetric label="Memory" value={state?.memoryPercent} unit="%" threshold={85} />
          <DemoMetric label="Latency" value={state?.latencyMs} unit="ms" threshold={1000} />
          <DemoMetric
            label="Error rate"
            value={state?.errorRate != null ? state.errorRate * 100 : null}
            unit="%"
            threshold={10}
          />
          <DemoMetric label="Retries" value={state?.retriesPerMinute} unit="/min" threshold={30} />
          <DemoMetric label="Queue" value={state?.queueDepth} unit="" threshold={50} />
        </div>

        <div className="demo-state-line">
          <span>
            Phase: <strong>{state?.phase || 'Normal'}</strong>
          </span>
          <span>
            Retry loop: <strong>{state?.retryLoopEnabled ? 'ENABLED' : 'disabled'}</strong>
          </span>
          <span>
            Workers: <strong>{state?.workerConcurrency ?? '--'}</strong>
          </span>
          {state?.lastTickAt && <span>updated {relativeTime(state.lastTickAt)}</span>}
        </div>
      </section>

      {demoIncident && (
        <section className="section-card demo-incident-card">
          <div className="demo-incident-head">
            <span className="incident-key">{demoIncident.incidentKey}</span>
            <SeverityBadge severity={demoIncident.severity} />
            <StatusBadge status={demoIncident.status} />
            {pinnedId && (
              <button type="button" className="secondary-btn" onClick={() => setPinnedId(null)}>
                Follow latest
              </button>
            )}
          </div>

          <IncidentDetail query={detail} actions={actions} />
        </section>
      )}

      {!demoIncident && state?.running && (
        <section className="section-card">
          <p className="panel-pending-text">
            Scenario running. Waiting for the detection engine to correlate enough signals to open
            an incident.
          </p>
        </section>
      )}
    </div>
  );
}

/**
 * Maps live backend state onto the visible pipeline.
 *
 * Deliberately derived from the incident's own status and the demo phase, never from a local
 * timer: the display cannot run ahead of what the backend has actually done.
 */
function currentStage(demoState, incident) {
  if (incident) {
    switch (incident.status) {
      case IncidentStatus.Resolved:
        return 10;
      case IncidentStatus.Verifying:
        return 9;
      case IncidentStatus.Remediating:
        return 8;
      case IncidentStatus.AwaitingApproval:
        return 7;
      case IncidentStatus.RecommendationReady:
        return 6;
      case IncidentStatus.Predicted:
        return 5;
      case IncidentStatus.Diagnosed:
        return 4;
      case IncidentStatus.Investigating:
        return 3;
      case IncidentStatus.Detected:
        return 2;
      default:
        // Failed, Rejected and Cancelled all stop the pipeline where they happened; the incident
        // detail below explains why.
        return 2;
    }
  }

  if (demoState?.running) return 1;
  return 0;
}

function DemoMetric({ label, value, unit, threshold }) {
  const breaching = value !== null && value !== undefined && threshold && value > threshold;

  return (
    <div className={`demo-metric ${breaching ? 'breaching' : ''}`}>
      <span className="demo-metric-label">{label}</span>
      <span className="demo-metric-value">{formatMetricValue(value, unit)}</span>
      <div className="demo-metric-track">
        <div
          className="demo-metric-fill"
          style={{ width: `${Math.min(100, threshold ? ((value || 0) / threshold) * 60 : 0)}%` }}
        />
      </div>
    </div>
  );
}
