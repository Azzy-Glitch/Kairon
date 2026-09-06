import React, { useEffect, useMemo, useRef, useState } from 'react';
import { telemetryApi, sdkApi } from '../api/index';
import { IconServer, IconRefresh, IconCopy, IconCheck, IconLink } from './Icons';
import { useToast } from './Toast';
import Badge from './ui/Badge';
import Button from './ui/Button';
import MetricTile from './ui/MetricTile';

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

// Legacy SDK rows identify projects and services, not authenticated SDK languages.
function TelemetrySection({ label, projects }) {
  const [projectId, setProjectId] = useState('');
  const [service, setService] = useState('');
  const generation = useRef(0);
  const [incidents, setIncidents] = useState([]);
  const [metrics, setMetrics] = useState([]);
  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [copied, setCopied] = useState(false);
  const { addToast } = useToast();

  const load = async () => {
    if (!projectId) return;
    const request = ++generation.current;
    setLoading(true);
    try {
      const [incidentRows, metricRows] = await Promise.all([
        telemetryApi.getTelemetryIncidents(projectId, service.trim() || undefined),
        telemetryApi.getMetrics(projectId, service.trim() || undefined)
      ]);
      if (request !== generation.current) return;
      setIncidents(incidentRows);
      setMetrics(metricRows);
      setLoaded(true);
    } catch (e) {
      if (request !== generation.current) return;
      addToast(`Failed to load ${label} telemetry: ` + e.message, 'error');
    } finally {
      if (request === generation.current) setLoading(false);
    }
  };

  useEffect(() => {
    generation.current++;
    setIncidents([]);
    setMetrics([]);
    setLoaded(false);
    if (!projectId) {
      setLoading(false);
      setIncidents([]);
      setMetrics([]);
      setLoaded(false);
      return;
    }
    load();
    return () => { generation.current++; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

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
            <p className="section-desc">Requests and metrics for the selected project. SDK language is not inferred from project or service names.</p>
          </div>
        </div>
        <Badge tone={connected ? 'healthy' : 'neutral'}>{connected ? 'Connected' : 'Not connected'}</Badge>
      </div>

      <div className="telemetry-project-row">
        <select
          className="approval-input telemetry-project-select"
          value={projectId}
          onChange={(e) => { setService(''); setProjectId(e.target.value); }}
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

        <input className="approval-input" aria-label="Service filter" placeholder="Exact service name (optional)"
          value={service} onChange={(e) => setService(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') load(); }} />
        <Button
          variant="ghost"
          size="compact"
          onClick={load}
          disabled={!projectId || loading}
          aria-label="Apply filter and refresh"
          title="Apply filter and refresh"
        >
          <IconRefresh className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
        </Button>
      </div>

      {!projectId ? (
        <p className="panel-pending-text telemetry-empty-hint">
          <IconLink className="w-4 h-4" />
          Select a project above, or pair an app on the "Connect an app" page to create one.
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

          <h5 className="subheading">Requests ({incidents.length})</h5>
          {incidents.length === 0 ? (
            <p className="panel-pending-text">No requests recorded for this selection.</p>
          ) : (
            <div className="table-responsive">
              <table className="custom-table">
                <thead>
                  <tr>
                    <th>Time</th>
                    <th>Service</th>
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
                      <td>{inc.service || inc.application || 'Unknown'}</td>
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

// Project-scoped telemetry with optional exact service filtering.
export default function TelemetryMonitor() {
  const [projects, setProjects] = useState([]);

  useEffect(() => {
    sdkApi.listProjects().then(setProjects).catch(() => setProjects([]));
  }, []);

  return (
    <div className="animate-fade-in telemetry-monitor">
      <TelemetrySection label="Project telemetry" projects={projects} />
    </div>
  );
}
