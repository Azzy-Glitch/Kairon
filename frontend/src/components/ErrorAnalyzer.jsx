import React, { useState } from 'react';
import { devopsApi } from '../api/index';
import { IconBug, IconSparkles, IconCopy, IconCheck, IconAlertTriangle, IconTerminal } from './Icons';
import { useToast } from './Toast';

const PRESETS = [
  {
    name: "Null Pointer Crash",
    log: `TypeError: Cannot read properties of undefined (reading 'userId')
    at authenticateUser (/app/src/middleware/auth.js:42:28)
    at Layer.handle [as handle_request] (/app/node_modules/express/lib/router/layer.js:95:5)
    at next (/app/node_modules/express/lib/router/route.js:144:13)
    at Route.dispatch (/app/node_modules/express/lib/router/route.js:114:3)`
  },
  {
    name: "DB Pool Exhaustion",
    log: `Npgsql.NpgsqlException (0x80004005): The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)
    at Npgsql.NpgsqlConnector.Connect(NpgsqlTimeout timeout)
    at Npgsql.ConnectorPool.Allocate(NpgsqlConnection conn, NpgsqlTimeout timeout)
    at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.OpenInternalAsync()`
  },
  {
    name: "Redis OOM Kill",
    log: `RedisException: OOM command not allowed when used memory > 'maxmemory'. 
    Used: 2147483648 bytes, Max: 2147483648 bytes.
    at StackExchange.Redis.ConnectionMultiplexer.Execute[T]
    at ServiceCache.SetAsync(String key, Byte[] payload)`
  }
];

export default function ErrorAnalyzer() {
  const [log, setLog] = useState(PRESETS[0].log);
  const [res, setRes] = useState(null);
  const [loading, setLoading] = useState(false);
  const [copiedIndex, setCopiedIndex] = useState(null);
  const { addToast } = useToast();

  const analyze = async () => {
    if (!log.trim()) {
      addToast('Please enter an error log or stack trace', 'warning');
      return;
    }
    setLoading(true);
    try {
      const data = await devopsApi.analyzeError(log);
      setRes(data);
      addToast('Incident analyzed successfully', 'success');
    } catch (e) {
      addToast(e.message || 'Failed to analyze error', 'error');
    } finally {
      setLoading(false);
    }
  };

  const copyToClipboard = (text, idx = null) => {
    navigator.clipboard.writeText(text);
    if (idx !== null) {
      setCopiedIndex(idx);
      setTimeout(() => setCopiedIndex(null), 2000);
    }
    addToast('Copied to clipboard!', 'info');
  };

  const getSeverityStyle = (s) => {
    const sev = (s || '').toLowerCase();
    if (sev === 'critical') return { bg: 'var(--critical-soft)', border: 'var(--critical)', text: 'var(--critical)', badge: 'var(--critical)' };
    if (sev === 'high') return { bg: 'var(--high-soft)', border: 'var(--high)', text: 'var(--high)', badge: 'var(--high)' };
    if (sev === 'medium') return { bg: 'var(--medium-soft)', border: 'var(--medium)', text: 'var(--medium)', badge: 'var(--medium)' };
    return { bg: 'var(--healthy-soft)', border: 'var(--healthy)', text: 'var(--healthy)', badge: 'var(--healthy)' };
  };

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge error-badge">
            <IconBug className="w-6 h-6 tone-critical" />
          </div>
          <div>
            <h3>AI Root Cause & Stack Trace Diagnostics</h3>
            <p className="section-desc">Automated incident triage, severity ranking, root-cause isolation & patch synthesis</p>
          </div>
        </div>

        <div className="preset-selector">
          <span className="preset-label">Quick Samples:</span>
          {PRESETS.map((p, i) => (
            <button
              key={i}
              type="button"
              className="preset-btn"
              onClick={() => { setLog(p.log); setRes(null); }}
            >
              {p.name}
            </button>
          ))}
        </div>
      </div>

      <div className="editor-container">
        <div className="editor-header">
          <span className="editor-title">
            <IconTerminal className="w-4 h-4 mr-1 inline" /> Log Input / Production Stack Trace
          </span>
          {log && (
            <button className="editor-action-btn" onClick={() => setLog('')}>
              Clear
            </button>
          )}
        </div>
        <textarea
          rows={6}
          value={log}
          onChange={(e) => setLog(e.target.value)}
          placeholder="Paste raw stack trace, application exceptions, or container crash logs here..."
          className="code-textarea"
        />
      </div>

      <div className="action-bar">
        <button
          onClick={analyze}
          disabled={loading}
          className="primary-btn"
        >
          {loading ? (
            <>
              <span className="spinner"></span>
              <span>Decompiling & Triaging...</span>
            </>
          ) : (
            <>
              <IconSparkles className="w-4 h-4 mr-2" />
              <span>Diagnose Incident</span>
            </>
          )}
        </button>
      </div>

      {res && (
        <div className="results-panel animate-fade-in">
          <div className="results-banner" style={{
            backgroundColor: getSeverityStyle(res.severity).bg,
            borderColor: getSeverityStyle(res.severity).border
          }}>
            <div className="severity-info">
              <span className="status-pill" style={{
                backgroundColor: getSeverityStyle(res.severity).badge,
                color: 'var(--text-inverse)'
              }}>
                {res.severity ? res.severity.charAt(0).toUpperCase() + res.severity.slice(1).toLowerCase() : 'Analysis'}
              </span>
              <div className="severity-score-wrap">
                <span className="score-label">Impact Severity Score</span>
                <div className="score-meter-bar">
                  <div
                    className="score-meter-fill"
                    style={{
                      width: `${res.severity_score ?? 0}%`,
                      backgroundColor: getSeverityStyle(res.severity).badge
                    }}
                  />
                </div>
                <span className="score-value" style={{ color: getSeverityStyle(res.severity).text }}>
                  {res.severity_score != null ? `${res.severity_score}/100` : 'Not scored'}
                </span>
              </div>
            </div>
          </div>

          <div className="diagnostic-grid">
            <div className="diag-card primary-diag">
              <h4>
                <IconAlertTriangle className="w-4 h-4 tone-medium mr-2 inline" />
                Root Cause Analysis
              </h4>
              <p className="diag-text">{res.root_cause || 'No specific root cause identified.'}</p>
            </div>

            <div className="diag-card">
              <div className="card-header-flex">
                <h4>
                  <IconCheck className="w-4 h-4 tone-healthy mr-2 inline" />
                  Recommended Actionable Fixes
                </h4>
                {res.fixes && res.fixes.length > 0 && (
                  <button
                    className="small-btn"
                    onClick={() => copyToClipboard(res.fixes.join('\n'))}
                  >
                    <IconCopy className="w-3 h-3 mr-1 inline" /> Copy All
                  </button>
                )}
              </div>
              <ul className="fix-list">
                {res.fixes && res.fixes.length > 0 ? (
                  res.fixes.map((f, i) => (
                    <li key={i} className="fix-item">
                      <span className="fix-number">{i + 1}</span>
                      <span className="fix-text">{f}</span>
                      <button
                        className="copy-chip-btn"
                        title="Copy fix"
                        onClick={() => copyToClipboard(f, i)}
                      >
                        {copiedIndex === i ? <IconCheck className="w-3 h-3 tone-healthy" /> : <IconCopy className="w-3 h-3" />}
                      </button>
                    </li>
                  ))
                ) : (
                  <li className="fix-item">No specific patch generated. Review stack trace manually.</li>
                )}
              </ul>
            </div>

            <div className="diag-card span-full">
              <h4>
                <IconSparkles className="w-4 h-4 tone-accent mr-2 inline" />
                Architectural Prevention & Best Practice
              </h4>
              <p className="prevention-text">{res.prevention || 'Maintain proactive boundary checks and unit tests.'}</p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}