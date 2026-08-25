import { useState, useEffect } from 'react';
import { telemetryApi } from '../api/index';
import { IconServer, IconRefresh, IconZap } from './Icons';
import { useToast } from './Toast';

export default function TelemetryMonitor() {
  const [incidents, setIncidents] = useState([]);
  const [metrics, setMetrics] = useState([]);
  const [projectId, setProjectId] = useState('550e8400-e29b-41d4-a716-446655440000');
  const [loading, setLoading] = useState(false);
  const { addToast } = useToast();

  const load = async () => {
    setLoading(true);
    try {
      const [incidentRows, metricRows] = await Promise.all([
        telemetryApi.getTelemetryIncidents(projectId),
        telemetryApi.getMetrics(projectId)
      ]);
      setIncidents(incidentRows);
      setMetrics(metricRows);
    } catch (e) {
      addToast('Failed to load telemetry: ' + e.message, 'error');
    } finally {
      setLoading(false);
    }
  };

  const seed = async () => {
    try {
      await telemetryApi.postTelemetryIncident({
        projectId, endpoint: '/payment', method: 'POST',
        statusCode: 500, duration: 3200, error: 'Timeout', timestamp: new Date().toISOString()
      });
      await telemetryApi.postMetric({
        projectId, cpuPercent: 92.5, memoryPercent: 78.0, responseTimeMs: 1200
      });
      addToast('Test telemetry seeded', 'success');
      load();
    } catch (e) {
      addToast('Seed failed: ' + e.message, 'error');
    }
  };

  useEffect(() => { load(); }, [projectId]);

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconServer className="w-6 h-6 text-sky-500" />
          </div>
          <div>
            <h3>Live Telemetry Monitor</h3>
            <p className="section-desc">Raw incidents and metrics captured automatically by the AIDIP SDK, straight from the Telemetry API</p>
          </div>
        </div>

        <div className="flex-actions">
          <button className="secondary-btn" onClick={load} disabled={loading}>
            <IconRefresh className={`w-4 h-4 mr-1 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </button>
          <button className="secondary-btn" onClick={seed}>
            <IconZap className="w-4 h-4 mr-1" />
            Seed Test Data
          </button>
        </div>
      </div>

      <div className="editor-container">
        <div className="editor-header">
          <span className="editor-title">Project ID</span>
        </div>
        <input
          value={projectId}
          onChange={(e) => setProjectId(e.target.value)}
          placeholder="Project ID"
          className="code-textarea"
          style={{ minHeight: 'auto', padding: '10px 12px' }}
        />
      </div>

      <h4 className="subheading" style={{ marginTop: 24 }}>Incidents ({incidents.length})</h4>
      {incidents.length === 0 ? (
        <div className="empty-state">
          <IconServer className="w-10 h-10 text-slate-400 mb-2" />
          <p>No incidents recorded yet for this project ID</p>
        </div>
      ) : (
        <div className="table-responsive">
          <table className="custom-table">
            <thead>
              <tr><th>Time</th><th>Endpoint</th><th>Status</th><th>Duration</th><th>Error</th></tr>
            </thead>
            <tbody>
              {incidents.map((inc, i) => (
                <tr key={i}>
                  <td>{new Date(inc.timestamp).toLocaleTimeString()}</td>
                  <td><code className="path-code">{inc.endpoint}</code></td>
                  <td style={{ color: inc.statusCode >= 500 ? '#dc2626' : inc.statusCode >= 400 ? '#ca8a04' : '#16a34a', fontWeight: 600 }}>{inc.statusCode}</td>
                  <td>{inc.durationMs}ms</td>
                  <td>{inc.errorMessage || '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h4 className="subheading" style={{ marginTop: 24 }}>Metrics ({metrics.length})</h4>
      {metrics.length === 0 ? (
        <div className="empty-state">
          <IconServer className="w-10 h-10 text-slate-400 mb-2" />
          <p>No metrics recorded yet for this project ID</p>
        </div>
      ) : (
        <div className="table-responsive">
          <table className="custom-table">
            <thead>
              <tr><th>Time</th><th>CPU %</th><th>Memory %</th><th>Response ms</th></tr>
            </thead>
            <tbody>
              {metrics.map((m, i) => (
                <tr key={i}>
                  <td>{new Date(m.timestamp).toLocaleTimeString()}</td>
                  <td>{m.cpuPercent}%</td>
                  <td>{m.memoryPercent}%</td>
                  <td>{m.responseTimeMs}ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
