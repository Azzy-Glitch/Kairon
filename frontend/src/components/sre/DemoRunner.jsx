import React, { useEffect, useMemo, useRef, useState } from 'react';
import { SeverityBadge, StatusBadge } from './Badges';
import { ErrorState } from './StateViews';
import PhaseAnnotatedLineChart from '../ui/charts/PhaseAnnotatedLineChart';
import { useRollingBuffer } from '../../hooks/useRollingBuffer';
import { useDemo } from '../../hooks/useDemo';
import { useIncident, useIncidents } from '../../hooks/useIncidents';
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
  { key: 'normal', label: 'Normal' },
  { key: 'failure', label: 'Failure' },
  { key: 'detected', label: 'Detected' },
  { key: 'investigating', label: 'Investigating' },
  { key: 'rootcause', label: 'Root cause' },
  { key: 'prediction', label: 'Prediction' },
  { key: 'recommendation', label: 'Recommendation' },
  { key: 'approval', label: 'Approval' },
  { key: 'remediation', label: 'Remediation' },
  { key: 'verification', label: 'Verification' },
  { key: 'resolved', label: 'Resolved' }
];

const DEMO_SERIES_KEYS = [
  { dataKey: 'cpu', name: 'CPU %' },
  { dataKey: 'latency', name: 'Latency ms' },
  { dataKey: 'errorRate', name: 'Error rate %' },
  { dataKey: 'retries', name: 'Retries/min' }
];

export default function DemoRunner({ onOpenIncident }) {
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

  const state = demo.data;
  const reachedStage = currentStage(state, detail.data);

  // The demo's money shot: all four simulation metrics on one shared time axis, with a vertical
  // marker at the moment each phase started. Fed from the same poll useDemo() already runs (no
  // second interval) - one sample appended per real tick, reset when a fresh run starts.
  const [chartSamples, pushSample, resetSamples] = useRollingBuffer(120);
  const [phaseMarkers, setPhaseMarkers] = useState([]);
  const lastTickRef = useRef(null);
  const lastPhaseRef = useRef(null);

  useEffect(() => {
    if (!state?.running) {
      if (lastPhaseRef.current !== null) {
        resetSamples();
        setPhaseMarkers([]);
        lastPhaseRef.current = null;
        lastTickRef.current = null;
      }
      return;
    }

    const tickKey = state.lastTickAt || null;
    if (tickKey === lastTickRef.current) return;
    lastTickRef.current = tickKey;

    const label = state.lastTickAt ? new Date(state.lastTickAt).toLocaleTimeString() : '';
    const phase = state.phase || 'Normal';

    pushSample({
      t: label,
      cpu: state.cpuPercent ?? null,
      latency: state.latencyMs ?? null,
      errorRate: state.errorRate != null ? state.errorRate * 100 : null,
      retries: state.retriesPerMinute ?? null
    });

    if (phase !== lastPhaseRef.current) {
      lastPhaseRef.current = phase;
      setPhaseMarkers((prev) => [...prev, { t: label, label: phase }]);
    }
    // pushSample/resetSamples are stable (useCallback with a fixed capacity) - omitting them
    // from deps avoids re-running this effect on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state?.running, state?.lastTickAt, state?.phase, state?.cpuPercent, state?.latencyMs, state?.errorRate, state?.retriesPerMinute]);

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
                investigation, approval and remediation are all real.
              </p>
              <div className="demo-info-note">
                <span className="demo-info-note-icon" aria-hidden="true">
                  <InfoIcon />
                </span>
                <span>Only the failing service is simulated, and only inside the demo environment.</span>
              </div>
            </div>
          </div>

          <div className="flex-actions">
            <button
              type="button"
              className="primary-btn"
              onClick={state?.running ? demo.stop : demo.start}
              disabled={demo.busy === 'start' || demo.busy === 'stop'}
              title={state?.running ? 'Stop the running simulation' : undefined}
            >
              {demo.busy === 'start' ? (
                'Starting...'
              ) : demo.busy === 'stop' ? (
                'Stopping...'
              ) : state?.running ? (
                <>
                  <span className="btn-spinner" aria-hidden="true" />
                  Running… stop
                </>
              ) : (
                'Run Incident Simulation'
              )}
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

        {/* CPU, latency, error rate and retries now live in the trend chart below - a static tile
            repeating the same number the chart's own last point already shows would be the exact
            "static metric card" the redesign brief asks to replace with a live chart (section 7).
            Memory and queue depth aren't part of that chart, so they keep an at-a-glance tile. */}
        <div className="demo-metrics">
          <DemoMetric label="Memory" value={state?.memoryPercent} unit="%" threshold={85} />
          <DemoMetric label="Queue" value={state?.queueDepth} unit="" threshold={50} />
        </div>

        <div className="demo-state-line">
          <span>
            Phase: <strong>{state?.phase || 'Normal'}</strong>
          </span>
          <span>
            Retry loop: <strong>{state?.retryLoopEnabled ? 'Enabled' : 'Disabled'}</strong>
          </span>
          <span>
            Workers: <strong>{state?.workerConcurrency ?? '--'}</strong>
          </span>
          {state?.lastTickAt && <span>updated {relativeTime(state.lastTickAt)}</span>}
        </div>

        {chartSamples.length >= 2 ? (
          <div className="demo-trend-chart">
            <span className="block-label">Simulation metrics over time</span>
            <PhaseAnnotatedLineChart data={chartSamples} seriesKeys={DEMO_SERIES_KEYS} phaseMarkers={phaseMarkers} height={260} />
          </div>
        ) : (
          state?.running && (
            <p className="panel-pending-text demo-trend-pending">
              Collecting samples - the trend chart appears once a couple of readings have come in.
            </p>
          )
        )}
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

          <div className="demo-incident-summary">
            <div className="demo-incident-summary-title">{demoIncident.title}</div>
            <div className="demo-incident-summary-meta">
              {demoIncident.service ? `${demoIncident.service} • ` : ''}
              Detected {relativeTime(demoIncident.detectedAt)}
            </div>
            {onOpenIncident ? (
              <button
                type="button"
                className="primary-btn"
                onClick={() => onOpenIncident(demoIncident.id)}
              >
                Open full incident
              </button>
            ) : (
              <p className="panel-pending-text">Full incident detail lives on the Incidents page.</p>
            )}
          </div>
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
        // Failed, Rejected and Cancelled all stop the pipeline where they happened; the full
        // incident (opened via the summary card below) explains why.
        return 2;
    }
  }

  if (demoState?.running) return 1;
  return 0;
}

/** Small "i in a circle" glyph for the honesty-note callout - deliberately not one of the shared
 * status icons, since this note is informational rather than a warning or an alert. */
function InfoIcon() {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" fill="none" aria-hidden="true">
      <circle cx="8" cy="8" r="7" stroke="currentColor" strokeWidth="1.4" />
      <line x1="8" y1="7" x2="8" y2="11.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <circle cx="8" cy="4.8" r="0.9" fill="currentColor" />
    </svg>
  );
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
