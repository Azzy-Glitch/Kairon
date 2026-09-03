import React, { useState } from 'react';
import { devopsApi } from '../api/index';
import { IconPredict, IconSparkles, IconAlertTriangle, IconTerminal } from './Icons';
import { useToast } from './Toast';

const PRESETS = [
  {
    name: "Cascading Timeout Storm",
    recent: [
      "2026-08-19T01:30:00Z [auth-service] DB Connection latency 450ms (warning: normal < 50ms)",
      "2026-08-19T01:31:12Z [payment-gateway] HTTP 504 Gateway Timeout on upstream checkout",
      "2026-08-19T01:32:05Z [order-worker] Queue depth exceeded 15,000 unacknowledged jobs"
    ].join('\n'),
    current: "2026-08-19T01:33:00Z [api-gateway] HTTP 503 Service Unavailable: Circuit Breaker TRIPPED"
  },
  {
    name: "Memory Leak Cascade",
    recent: [
      "2026-08-19T00:10:00Z [node-app-1] Heap usage 680MB / 1024MB",
      "2026-08-19T00:25:00Z [node-app-1] Garbage collection pauses exceeding 800ms",
      "2026-08-19T00:40:00Z [node-app-1] Heap usage 990MB / 1024MB (96.7% capacity)"
    ].join('\n'),
    current: "2026-08-19T00:42:15Z [k8s-kubelet] OOMKilled process 1024 (node) in pod app-core-7f89b"
  }
];

export default function Predictor() {
  const [recent, setRecent] = useState(PRESETS[0].recent);
  const [cur, setCur] = useState(PRESETS[0].current);
  const [res, setRes] = useState(null);
  const [loading, setLoading] = useState(false);
  const { addToast } = useToast();

  const predict = async () => {
    if (!cur.trim()) {
      addToast('Please provide the current triggering log', 'warning');
      return;
    }
    const logList = recent
      .split('\n')
      .map(l => l.trim())
      .filter(l => l.length > 0);

    setLoading(true);
    try {
      const data = await devopsApi.predict(logList, cur);
      setRes(data);
      addToast('Failure risk calculated', 'success');
    } catch (e) {
      addToast(e.message || 'Prediction failed', 'error');
    } finally {
      setLoading(false);
    }
  };

  const getRiskColor = (score) => {
    if (score >= 70) return 'var(--critical)';
    if (score >= 40) return 'var(--medium)';
    return 'var(--healthy)';
  };

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge predict-badge">
            <IconPredict className="w-6 h-6 tone-accent" />
          </div>
          <div>
            <h3>Predictive Anomaly & Failure Risk Radar</h3>
            <p className="section-desc">Correlate time-series telemetry streams to predict cascading outages and quantify downstream risk</p>
          </div>
        </div>

        <div className="preset-selector">
          <span className="preset-label">Scenarios:</span>
          {PRESETS.map((p, i) => (
            <button
              key={i}
              type="button"
              className="preset-btn"
              onClick={() => {
                setRecent(p.recent);
                setCur(p.current);
                setRes(null);
              }}
            >
              {p.name}
            </button>
          ))}
        </div>
      </div>

      <div className="telemetry-input-grid">
        <div className="editor-container">
          <div className="editor-header">
            <span className="editor-title">
              <IconTerminal className="w-4 h-4 mr-1 inline tone-accent" />
              Preceding Log Sequence (One per line)
            </span>
          </div>
          <textarea
            rows={5}
            value={recent}
            onChange={(e) => setRecent(e.target.value)}
            placeholder="Preceding telemetry logs..."
            className="code-textarea"
          />
        </div>

        <div className="editor-container">
          <div className="editor-header">
            <span className="editor-title">
              <IconAlertTriangle className="w-4 h-4 mr-1 inline tone-critical" />
              Current Incident Trigger
            </span>
          </div>
          <textarea
            rows={3}
            value={cur}
            onChange={(e) => setCur(e.target.value)}
            placeholder="Latest critical event log..."
            className="code-textarea"
          />
        </div>
      </div>

      <div className="action-bar">
        <button
          onClick={predict}
          disabled={loading}
          className="primary-btn"
        >
          {loading ? (
            <>
              <span className="spinner"></span>
              <span>Calculating Risk Horizon...</span>
            </>
          ) : (
            <>
              <IconSparkles className="w-4 h-4 mr-2" />
              <span>Forecast System Risk</span>
            </>
          )}
        </button>
      </div>

      {res && (
        <div className="results-panel animate-fade-in">
          <div className="risk-display-grid">
            <div
              className="risk-gauge-card"
              style={{
                borderColor: getRiskColor(res.failure_risk_score),
                backgroundColor: 'var(--surface)'
              }}
            >
              <div className="gauge-dial">
                <span
                  className="gauge-val"
                  style={{ color: getRiskColor(res.failure_risk_score) }}
                >
                  {res.failure_risk_score}%
                </span>
                <span className="gauge-sub">Risk factor</span>
              </div>
              <div
                className="risk-status-pill"
                style={{
                  backgroundColor: getRiskColor(res.failure_risk_score),
                  color: 'var(--text-inverse)'
                }}
              >
                {res.risk_level ? `${res.risk_level.charAt(0).toUpperCase()}${res.risk_level.slice(1).toLowerCase()} risk` : 'Evaluating risk'}
              </div>
            </div>

            <div className="risk-explanation-card">
              <h4>
                <IconAlertTriangle className="w-5 h-5 tone-medium mr-2 inline" />
                Cascade Assessment & AI Reasoning
              </h4>
              <p className="reasoning-text">{res.reasoning || 'Cascading failure analysis completed.'}</p>
              <div className="prevention-alert">
                <strong>Recommended Mitigation:</strong> Implement exponential backoff, rate limits, and isolate degraded workers.
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
