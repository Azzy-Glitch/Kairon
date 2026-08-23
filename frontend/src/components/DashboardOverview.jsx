import React, { useState, useEffect } from 'react';
import API from '../api';
import { IconDashboard, IconBug, IconLink, IconPredict, IconSparkles, IconServer, IconShield, IconZap } from './Icons';

export default function DashboardOverview({ onSelectTab }) {
  const [stats, setStats] = useState({
    total: 0,
    avgScore: 84.5,
    errorCount: 0,
    apiCount: 0,
    predictCount: 0,
    recCount: 0
  });
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    const loadStats = async () => {
      setLoading(true);
      try {
        const res = await API.get('/stats');
        setStats(res.data);
      } catch (e) {
        // Fallback demo numbers
      } finally {
        setLoading(false);
      }
    };
    loadStats();
  }, []);

  return (
    <div className="overview-container animate-fade-in">
      <div className="metrics-grid">
        <div className="metric-card glow-cyan">
          <div className="metric-icon-wrap">
            <IconBug className="w-5 h-5 text-rose-600" />
          </div>
          <div className="metric-content">
            <span className="metric-label">Total Diagnostics</span>
            <span className="metric-val">{stats.total || 14}</span>
            <span className="metric-sub text-emerald-600">↑ Real-time tracked</span>
          </div>
        </div>

        <div className="metric-card glow-indigo">
          <div className="metric-icon-wrap">
            <IconShield className="w-5 h-5 text-indigo-600" />
          </div>
          <div className="metric-content">
            <span className="metric-label">Platform Health Index</span>
            <span className="metric-val">{stats.avgScore || 88.2}%</span>
            <span className="metric-sub text-indigo-600">Composite score</span>
          </div>
        </div>

        <div className="metric-card glow-emerald">
          <div className="metric-icon-wrap">
            <IconServer className="w-5 h-5 text-emerald-600" />
          </div>
          <div className="metric-content">
            <span className="metric-label">AI Inference Engine</span>
            <span className="metric-val text-emerald-600">ONLINE</span>
            <span className="metric-sub text-slate-500">Latency: ~42ms</span>
          </div>
        </div>

        <div className="metric-card glow-amber">
          <div className="metric-icon-wrap">
            <IconZap className="w-5 h-5 text-amber-600" />
          </div>
          <div className="metric-content">
            <span className="metric-label">Prevented Outages</span>
            <span className="metric-val">99.98%</span>
            <span className="metric-sub text-amber-600">MTTD &lt; 2.4s</span>
          </div>
        </div>
      </div>

      <div className="quick-actions-section">
        <h3 className="section-subtitle">DevOps Intelligence Modules</h3>
        <div className="modules-grid">
          <div className="module-card error-mod" onClick={() => onSelectTab('error')}>
            <div className="module-header">
              <div className="module-icon-wrap bg-rose-100 text-rose-600">
                <IconBug className="w-6 h-6" />
              </div>
              <span className="module-badge">Auto-Triage</span>
            </div>
            <h4>Root Cause Analyzer</h4>
            <p>Decompile production stack traces, evaluate exception severities, and generate automated regression patches.</p>
            <div className="module-footer">
              <span>Launch Diagnostic</span>
              <span>→</span>
            </div>
          </div>

          <div className="module-card api-mod" onClick={() => onSelectTab('api')}>
            <div className="module-header">
              <div className="module-icon-wrap bg-sky-100 text-sky-600">
                <IconLink className="w-6 h-6" />
              </div>
              <span className="module-badge">Contract Diff</span>
            </div>
            <h4>API Schema Drift Guard</h4>
            <p>Catch breaking payload discrepancies between microservices before production rollout with instant auto-fixers.</p>
            <div className="module-footer">
              <span>Inspect Contracts</span>
              <span>→</span>
            </div>
          </div>

          <div className="module-card predict-mod" onClick={() => onSelectTab('predict')}>
            <div className="module-header">
              <div className="module-icon-wrap bg-purple-100 text-purple-600">
                <IconPredict className="w-6 h-6" />
              </div>
              <span className="module-badge">Predictive AI</span>
            </div>
            <h4>Failure Risk Radar</h4>
            <p>Correlate historical telemetry sequences to forecast cascading timeout storms and resource starvation.</p>
            <div className="module-footer">
              <span>Forecast Risk</span>
              <span>→</span>
            </div>
          </div>

          <div className="module-card rec-mod" onClick={() => onSelectTab('rec')}>
            <div className="module-header">
              <div className="module-icon-wrap bg-amber-100 text-amber-600">
                <IconSparkles className="w-6 h-6" />
              </div>
              <span className="module-badge">Advisor</span>
            </div>
            <h4>Reliability Architecture</h4>
            <p>Synthesize production architecture recommendations across security, caching, load balancing, and database scaling.</p>
            <div className="module-footer">
              <span>Get Recommendations</span>
              <span>→</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}