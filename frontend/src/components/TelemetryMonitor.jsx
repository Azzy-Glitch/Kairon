import React, { useEffect, useMemo, useState } from 'react';
import { telemetryApi, sdkApi } from '../api/index';
import { IconServer, IconRefresh, IconCopy, IconCheck, IconZap, IconLink } from './Icons';
import { useToast } from './Toast';
import Badge from './ui/Badge';
import Button from './ui/Button';
import MetricTile from './ui/MetricTile';

const SOURCES = [
  { id: 'dotnet', label: '.NET' },
  { id: 'python', label: 'Python' }
];

function round(value) {
  if (value === null || value === undefined || Number.isNaN(value)) return null;
  return Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
}

function toSeries(rows, pick) {
  return rows.map((r) => ({ t: new Date(r.timestamp).toLocaleTimeString(), v: round(pick(r)) }));
}

function StatusCodeBadge({ code }) {
  const tone = code >= 500 ? 'critical' : code >= 400 ? 'medium' : 'healthy';
  return <Badge tone={tone}>{code}</Badge>;
}

/**
 * One SDK-source panel (redesign brief section 8: Live telemetry). The backend has no per-project
 * SdkType field (Project/ProjectApiCredential carry only Id/Name/CreatedAt - confirmed by reading
 * ProjectsController directly rather than assuming), so this section lets the operator pick which
 * project represents ".NET" vs "Python" from the same project list the Connect an app page already
 * uses, instead of a free-text GUID field. "Connected" is an honest, locally-derived signal: this
 * project has returned at least one telemetry row on its most recent load.
 */
function TelemetrySection({ label, projects }) {
  const [projectId, setProjectId] = useState('');
  const [incidents, setIncidents] = useState([]);
  const [metrics, setMetrics] = useState([]);
  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [copied, setCopied] = useState(false);
  const { addToast } = useToast();

  const load = async () => {
    if (!projectId) return;
    setLoading(true);
    try {
      const [incidentRows, metricRows] = await Promise.all([
        telemetryApi.getTelemetryIncidents(projectId),
        telemetryApi.getMetrics(projectId)
      ]);
      setIncidents(incidentRows);
      setMetrics(metricRows);
      setLoaded(true);
    } catch (e) {
      addToast(`Failed to load ${label} telemetry: ` + e.message, 'error');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (!projectId) {
      setIncidents([]);
      setMetrics([]);
      setLoaded(false);
      return;
    }
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const seed = async () => {
    if (!projectId) return;
    try {
      await telemetryApi.postTelemetryIncident({
        projectId,
        endpoint: '/payment',
        method: 'POST',
        statusCode: 500,
        duration: 3200,
        error: 'Timeout',
        timestamp: new Date().toISOString()
      });
      await telemetryApi.postMetric({ projectId, cpuPercent: 92.5, memoryPercent: 78.0, responseTimeMs: 1200 });
      addToast('Test telemetry seeded', 'success');
      load();
    } catch (e) {
      addToast('Seed failed: ' + e.message, 'error');
    }
  };

  const copyProjectId = () => {
    if (!projectId) return;
    navigator.clipboard.writeText(projectId);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const connected = loaded && (incidents.length > 0 || metrics.length > 0);
  const cpuLatest = useMemo(() => metrics[0], [metrics]);
  const orderedMetrics = useMemo(() => [...metrics].reverse(), [metrics]);

  return (
    <section className="section-card telemetry-source-section">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconServer className="w-6 h-6 tone-low" />
          </div>
          <div>
            <h4>{label}</h4>
            <p className="section-desc">Raw incidents and metrics reported by the {label} SDK for this project.</p>
          </div>
        </div>
        <Badge tone={connected ? 'healthy' : 'neutral'}>{connected ? 'Connected' : 'Not connected'}</Badge>
      </div>

      <div className="telemetry-project-row">
        <select
          className="approval-input telemetry-project-select"
          value={projectId}
          onChange={(e) => setProjectId(e.target.value)}
          aria-label={`${label} project`}
        >
          <option value="">Select a project...</option>
          {(projects || []).map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>

        {projectId && (
          <>
            <code className="path-code telemetry-project-id" title={projectId}>
              {projectId}
            </code>
            <button
              type="button"
              className="small-btn"
              onClick={copyProjectId}
              title="Copy project ID"
              aria-label="Copy project ID"
            >
              {copied ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
            </button>
          </>
        )}

        <Button variant="secondary" size="compact" onClick={seed} disabled={!projectId}>
          <IconZap className="w-4 h-4 mr-1" />
          Seed test data
        </Button>
        <Button
          variant="ghost"
          size="compact"
          onClick={load}
          disabled={!projectId || loading}
          aria-label="Refresh"
          title="Refresh"
        >
          <IconRefresh className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      {!projectId ? (
        <p className="panel-pending-text telemetry-empty-hint">
          <IconLink className="w-4 h-4" />
          Select a project above, or pair a {label} app on the "Connect an app" page to create one.
        </p>
      ) : (
        <>
          <div className="telemetry-snapshot-grid telemetry-source-metrics">
            <MetricTile
              label="CPU usage"
              value={round(cpuLatest?.cpuPercent)}
              unit="%"
              data={toSeries(orderedMetrics, (m) => m.cpuPercent)}
              seriesColor="var(--series-1)"
              threshold={80}
              domain={[0, 100]}
            />
            <MetricTile
              label="Memory usage"
              value={round(cpuLatest?.memoryPercent)}
              unit="%"
              data={toSeries(orderedMetrics, (m) => m.memoryPercent)}
              seriesColor="var(--series-2)"
              threshold={85}
              domain={[0, 100]}
            />
            <MetricTile
              label="Response time"
              value={round(cpuLatest?.responseTimeMs)}
              unit="ms"
              data={toSeries(orderedMetrics, (m) => m.responseTimeMs)}
              seriesColor="var(--series-3)"
              threshold={1000}
              domain={['auto', 'auto']}
            />
          </div>

          <h5 className="subheading">Incidents ({incidents.length})</h5>
          {incidents.length === 0 ? (
            <p className="panel-pending-text">No incidents recorded yet for this project.</p>
          ) : (
            <div className="table-responsive">
              <table className="custom-table">
                <thead>
                  <tr>
                    <th>Time</th>
                    <th>Endpoint</th>
                    <th>Status</th>
                    <th>Duration</th>
                    <th>Error</th>
                  </tr>
                </thead>
                <tbody>
                  {incidents.map((inc, i) => (
                    <tr key={i}>
                      <td>{new Date(inc.timestamp).toLocaleTimeString()}</td>
                      <td>
                        <code className="path-code">{inc.endpoint}</code>
                      </td>
                      <td>
                        <StatusCodeBadge code={inc.statusCode} />
                      </td>
                      <td>{inc.durationMs}ms</td>
                      <td>{inc.errorMessage || '-'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/**
 * Live telemetry (renamed from "Observability" - redesign brief section 8). Source-grouped
 * sections: one panel per SDK language, each independently scoped to a project, with live metric
 * tiles instead of static numbers and a Badge instead of raw colored status-code text.
 */
export default function TelemetryMonitor() {
  const [projects, setProjects] = useState([]);

  useEffect(() => {
    sdkApi.listProjects().then(setProjects).catch(() => setProjects([]));
  }, []);

  return (
    <div className="animate-fade-in telemetry-monitor">
      {SOURCES.map((source) => (
        <TelemetrySection key={source.id} label={source.label} projects={projects} />
      ))}
    </div>
  );
}
