import React, { useState } from 'react';
import { devopsApi } from '../api/index';
import { IconSparkles, IconShield, IconZap, IconCopy, IconCheck, IconTerminal } from './Icons';
import { useToast } from './Toast';

const PRESETS = [
  {
    name: "High-Traffic Node.js API",
    context: "Node.js REST API on AWS ECS, 45,000 req/sec, unindexed PostgreSQL database, no Redis cache layer, direct synchronous third-party webhook calls."
  },
  {
    name: "Kubernetes Microservices Mesh",
    context: "18 microservices deployed on AWS EKS using gRPC and HTTP/2, lack of distributed tracing (no OpenTelemetry), default CPU limits leading to CFS throttling."
  },
  {
    name: "Serverless Event Architecture",
    context: "AWS Lambda functions writing directly to Aurora PostgreSQL without RDS Proxy, suffering cold start latency and database connection exhaustion spikes."
  }
];

export default function Recommender() {
  const [ctx, setCtx] = useState(PRESETS[0].context);
  const [res, setRes] = useState(null);
  const [loading, setLoading] = useState(false);
  const [copiedIdx, setCopiedIdx] = useState(null);
  const { addToast } = useToast();

  const recommend = async () => {
    if (!ctx.trim()) {
      addToast('Please provide system architecture context', 'warning');
      return;
    }
    setLoading(true);
    try {
      const data = await devopsApi.recommend(ctx);
      setRes(data);
      addToast('Architecture advisory generated', 'success');
    } catch (e) {
      addToast(e.message || 'Recommendation failed', 'error');
    } finally {
      setLoading(false);
    }
  };

  const copyRec = (text, i) => {
    navigator.clipboard.writeText(text);
    setCopiedIdx(i);
    setTimeout(() => setCopiedIdx(null), 2000);
    addToast('Recommendation copied!', 'info');
  };

  const getCategoryTheme = (cat) => {
    const c = (cat || '').toLowerCase();
    if (c === 'security') return { bg: 'var(--critical-soft)', border: 'var(--critical)', text: 'var(--critical)', label: 'Security', badgeBg: 'var(--critical)' };
    if (c === 'performance') return { bg: 'var(--low-soft)', border: 'var(--low)', text: 'var(--low)', label: 'Performance', badgeBg: 'var(--low)' };
    if (c === 'reliability') return { bg: 'var(--healthy-soft)', border: 'var(--healthy)', text: 'var(--healthy)', label: 'Reliability', badgeBg: 'var(--healthy)' };
    return { bg: 'var(--accent-soft)', border: 'var(--accent)', text: 'var(--accent)', label: 'Maintainability', badgeBg: 'var(--accent)' };
  };

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge rec-badge">
            <IconSparkles className="w-6 h-6 tone-medium" />
          </div>
          <div>
            <h3>AI Architecture & DevOps Reliability Advisor</h3>
            <p className="section-desc">Get tailored architectural blueprints, caching topologies, and security hardening guidelines</p>
          </div>
        </div>

        <div className="preset-selector">
          <span className="preset-label">Templates:</span>
          {PRESETS.map((p, i) => (
            <button
              key={i}
              type="button"
              className="preset-btn"
              onClick={() => { setCtx(p.context); setRes(null); }}
            >
              {p.name}
            </button>
          ))}
        </div>
      </div>

      <div className="editor-container">
        <div className="editor-header">
          <span className="editor-title">
            <IconTerminal className="w-4 h-4 mr-1 inline tone-medium" />
            Infrastructure & Workload Topology
          </span>
          {ctx && (
            <button className="editor-action-btn" onClick={() => setCtx('')}>
              Clear
            </button>
          )}
        </div>
        <textarea
          rows={5}
          value={ctx}
          onChange={(e) => setCtx(e.target.value)}
          placeholder="Describe your current tech stack, traffic patterns, bottlenecks, and database setup..."
          className="code-textarea"
        />
      </div>

      <div className="action-bar">
        <button
          onClick={recommend}
          disabled={loading}
          className="primary-btn"
        >
          {loading ? (
            <>
              <span className="spinner"></span>
              <span>Synthesizing Architecture Advisory...</span>
            </>
          ) : (
            <>
              <IconZap className="w-4 h-4 mr-2" />
              <span>Generate Recommendations</span>
            </>
          )}
        </button>
      </div>

      {res && res.recommendations && (
        <div className="results-panel animate-fade-in">
          <div className="recs-grid">
            {res.recommendations.map((r, i) => {
              const theme = getCategoryTheme(r.category);
              return (
                <div
                  key={i}
                  className="rec-card"
                  style={{
                    backgroundColor: theme.bg,
                    borderColor: theme.border
                  }}
                >
                  <div className="rec-card-header">
                    <span
                      className="category-pill"
                      style={{
                        backgroundColor: theme.badgeBg,
                        color: 'var(--text-inverse)'
                      }}
                    >
                      {theme.label}
                    </span>
                    <button
                      className="copy-chip-btn"
                      onClick={() => copyRec(r.suggestion, i)}
                    >
                      {copiedIdx === i ? <IconCheck className="w-3 h-3 tone-healthy" /> : <IconCopy className="w-3 h-3" />}
                    </button>
                  </div>
                  <p className="rec-body" style={{ color: 'var(--text)' }}>{r.suggestion}</p>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}