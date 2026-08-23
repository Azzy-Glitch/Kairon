import React, { useState } from 'react';
import API from '../api';
import { IconLink, IconSparkles, IconCheck, IconAlertTriangle, IconCopy, IconTerminal } from './Icons';
import { useToast } from './Toast';

const PRESETS = [
  {
    name: "User Auth Drift",
    expected: JSON.stringify({
      id: "integer",
      username: "string",
      email: "string",
      is_active: "boolean",
      role: "string"
    }, null, 2),
    actual: JSON.stringify({
      id: "10492",
      username: "alex_devops",
      email: "alex@cloudcorp.internal",
      is_active: "true",
      permissions: ["read", "write"]
    }, null, 2)
  },
  {
    name: "Payment Webhook Contract",
    expected: JSON.stringify({
      transaction_id: "string",
      amount_cents: "integer",
      currency: "string",
      status: "string"
    }, null, 2),
    actual: JSON.stringify({
      transaction_id: "tx_998124",
      amount_cents: "4999",
      currency: "USD",
      state: "completed"
    }, null, 2)
  }
];

export default function ApiValidator() {
  const [exp, setExp] = useState(PRESETS[0].expected);
  const [act, setAct] = useState(PRESETS[0].actual);
  const [res, setRes] = useState(null);
  const [loading, setLoading] = useState(false);
  const { addToast } = useToast();

  const validate = async () => {
    let parsedExp, parsedAct;
    try {
      parsedExp = JSON.parse(exp);
    } catch (e) {
      addToast('Expected schema is not valid JSON', 'warning');
      return;
    }
    try {
      parsedAct = JSON.parse(act);
    } catch (e) {
      addToast('Actual payload is not valid JSON', 'warning');
      return;
    }

    setLoading(true);
    try {
      const response = await API.post('/validate-api', {
        expected: parsedExp,
        actual: parsedAct
      });
      setRes(response.data);
      addToast('API contract validated successfully', 'success');
    } catch (e) {
      addToast(e.response?.data?.message || e.message || 'Validation request failed', 'error');
    } finally {
      setLoading(false);
    }
  };

  const formatJson = (setter, val) => {
    try {
      setter(JSON.stringify(JSON.parse(val), null, 2));
      addToast('JSON formatted', 'info');
    } catch (e) {
      addToast('Cannot format invalid JSON', 'warning');
    }
  };

  const getScoreColor = (score) => {
    if (score >= 85) return '#22c55e';
    if (score >= 60) return '#eab308';
    return '#ef4444';
  };

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge api-badge">
            <IconLink className="w-6 h-6 text-cyan-400" />
          </div>
          <div>
            <h3>API Contract & Schema Drift Validator</h3>
            <p className="section-desc">Detect breaking changes, runtime type drifts, and auto-synthesize backwards-compatible adapter code</p>
          </div>
        </div>

        <div className="preset-selector">
          <span className="preset-label">Presets:</span>
          {PRESETS.map((p, i) => (
            <button
              key={i}
              type="button"
              className="preset-btn"
              onClick={() => {
                setExp(p.expected);
                setAct(p.actual);
                setRes(null);
              }}
            >
              {p.name}
            </button>
          ))}
        </div>
      </div>

      <div className="split-editors">
        <div className="editor-container">
          <div className="editor-header">
            <span className="editor-title">
              <IconTerminal className="w-4 h-4 mr-1 inline text-cyan-400" />
              Target / Expected Schema
            </span>
            <button className="editor-action-btn" onClick={() => formatJson(setExp, exp)}>Format</button>
          </div>
          <textarea
            rows={8}
            value={exp}
            onChange={(e) => setExp(e.target.value)}
            placeholder='{"fieldName": "type", ...}'
            className="code-textarea"
          />
        </div>

        <div className="editor-container">
          <div className="editor-header">
            <span className="editor-title">
              <IconTerminal className="w-4 h-4 mr-1 inline text-indigo-400" />
              Actual Runtime Payload
            </span>
            <button className="editor-action-btn" onClick={() => formatJson(setAct, act)}>Format</button>
          </div>
          <textarea
            rows={8}
            value={act}
            onChange={(e) => setAct(e.target.value)}
            placeholder='{"fieldName": value, ...}'
            className="code-textarea"
          />
        </div>
      </div>

      <div className="action-bar">
        <button
          onClick={validate}
          disabled={loading}
          className="primary-btn pulse-glow"
        >
          {loading ? (
            <>
              <span className="spinner"></span>
              <span>Comparing Schema Signatures...</span>
            </>
          ) : (
            <>
              <IconSparkles className="w-4 h-4 mr-2" />
              <span>Validate Contract</span>
            </>
          )}
        </button>
      </div>

      {res && (
        <div className="results-panel animate-fade-in">
          <div className="score-summary-card">
            <div className="radial-score-box">
              <div
                className="radial-score-ring"
                style={{
                  borderColor: getScoreColor(res.reliability_score),
                  boxShadow: `0 0 20px ${getScoreColor(res.reliability_score)}33`
                }}
              >
                <span className="radial-score-number" style={{ color: getScoreColor(res.reliability_score) }}>
                  {res.reliability_score}
                </span>
                <span className="radial-score-label">Reliability</span>
              </div>
            </div>

            <div className="score-summary-text">
              <h4 style={{ color: getScoreColor(res.reliability_score) }}>
                {res.reliability_score === 100
                  ? 'Perfect Contract Match (100% Compliant)'
                  : res.reliability_score > 70
                  ? 'Minor Schema Drift Detected'
                  : 'Critical Breaking Contract Violations'}
              </h4>
              <p>
                {res.mismatches?.length === 0
                  ? 'All fields, expected types, and required payload signatures match without deviation.'
                  : `Identified ${res.mismatches?.length} schema mismatch(es) between provider contract and client consumer.`}
              </p>
            </div>
          </div>

          {res.mismatches && res.mismatches.length > 0 ? (
            <div className="mismatches-container">
              <h4 className="subheading">
                <IconAlertTriangle className="w-4 h-4 text-amber-400 mr-2 inline" />
                Detected Drift Inconsistencies
              </h4>
              <div className="table-responsive">
                <table className="custom-table">
                  <thead>
                    <tr>
                      <th>Property Path</th>
                      <th>Issue Type</th>
                      <th>Expected Type / Schema</th>
                      <th>Actual Value / Type</th>
                    </tr>
                  </thead>
                  <tbody>
                    {res.mismatches.map((m, i) => (
                      <tr key={i}>
                        <td><code className="path-code">{m.path}</code></td>
                        <td><span className="issue-badge">{m.issue}</span></td>
                        <td><span className="expected-pill">{m.expected || 'undefined'}</span></td>
                        <td><span className="actual-pill">{m.actual || 'undefined'}</span></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {res.suggestions && res.suggestions.length > 0 && (
                <div className="suggestions-box">
                  <h4>
                    <IconSparkles className="w-4 h-4 text-cyan-400 mr-2 inline" />
                    AI Remediation Recommendations
                  </h4>
                  <div className="suggestions-grid">
                    {res.suggestions.map((s, i) => (
                      <div key={i} className="suggestion-card">
                        <div className="suggestion-target">
                          <code>{s.path}</code>
                        </div>
                        <p className="suggestion-desc">{s.explanation}</p>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ) : (
            <div className="all-clear-box">
              <IconCheck className="w-12 h-12 text-emerald-400 mb-2" />
              <h3>Contract Fully Synchronized</h3>
              <p>No breaking schema changes detected. Payload is safe for production deployments.</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
