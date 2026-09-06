import React, { useState, useEffect } from 'react';
import { devopsApi } from '../api/index';
import { useHealth } from '../hooks/useHealth';
import Button from './ui/Button';
import { IconBug, IconLink, IconPredict, IconSparkles, IconServer, IconShield } from './Icons';

const TOOL_LABEL = { error: 'Root cause analyzer', api: 'API schema drift guard', predict: 'Failure risk radar', recommend: 'Reliability architecture' };

/** Whichever of the four tools has the most recorded runs - the one genuinely-available 4th
 * hero number in the stats response that wasn't already surfaced (errorCount/apiCount/
 * predictCount/recCount), rather than a fabricated metric. */
function mostUsedTool(stats) {
  if (!stats) return null;
  const counts = [
    ['error', stats.errorCount],
    ['api', stats.apiCount],
    ['predict', stats.predictCount],
    ['recommend', stats.recCount]
  ];
  const [type, count] = counts.reduce((best, cur) => (cur[1] > best[1] ? cur : best));
  return count > 0 ? { label: TOOL_LABEL[type], count } : null;
}

function HeroStatTile({ icon, label, value, sub, tone = 'neutral' }) {
  return (
    <div className={`primary-stat-card stat-${tone}`}>
      <div className="primary-stat-icon">{icon}</div>
      <div className="primary-stat-body">
        <span className="primary-stat-label">{label}</span>
        <span className="primary-stat-value">{value}</span>
        {sub && <span className="primary-stat-sub">{sub}</span>}
      </div>
    </div>
  );
}

export default function DashboardOverview({ onSelectTab }) {
  // No fallback numbers here on purpose: a diagnostic count of 0 is a real, valid answer (nothing
  // has been run yet), and showing a fabricated placeholder instead would misreport it as activity
  // that never happened (frontend PRD section 28 - do not fabricate a metric when the real one is
  // unavailable, or in this case IS available and genuinely zero).
  const [stats, setStats] = useState(null);
  const { health } = useHealth();

  useEffect(() => {
    devopsApi.getStats().then(setStats).catch(() => setStats(null));
  }, []);

  const topTool = mostUsedTool(stats);

  return (
    <div className="overview-container animate-fade-in">
      <div className="sre-primary-grid">
        <HeroStatTile
          icon={<IconBug className="w-5 h-5" />}
          label="Total diagnostics"
          value={stats ? stats.total : '--'}
          sub="Across all four tools below"
          tone="active"
        />
        <HeroStatTile
          icon={<IconShield className="w-5 h-5" />}
          label="Average diagnostic score"
          value={stats ? `${stats.avgScore}%` : '--'}
          sub="Mean score across every run recorded"
          tone="active"
        />
        <HeroStatTile
          icon={<IconServer className="w-5 h-5" />}
          label="AI inference engine"
          value={health.aiService ? 'Operational' : 'Offline'}
          sub={health.aiMode && health.aiMode !== 'unknown' ? `Provider: ${health.aiMode}` : ' '}
          tone={health.aiService ? 'good' : 'urgent'}
        />
        <HeroStatTile
          icon={<IconSparkles className="w-5 h-5" />}
          label="Most-used tool"
          value={topTool ? topTool.label : '--'}
          sub={topTool ? `${topTool.count} run(s)` : 'No diagnostics run yet'}
          tone="active"
        />
      </div>

      <div className="quick-actions-section">
        <h3 className="section-subtitle">DevOps Intelligence Modules</h3>
        <div className="modules-grid">
          <div className="module-card error-mod" onClick={() => onSelectTab('error')}>
            <div className="module-header">
              <div className="module-icon-wrap error-badge">
                <IconBug className="w-6 h-6" />
              </div>
              <span className="module-badge">Auto-Triage</span>
            </div>
            <h4>Root Cause Analyzer</h4>
            <p>Decompile production stack traces, evaluate exception severities, and generate automated regression patches.</p>
            <div className="module-footer">
              <Button variant="secondary" size="compact">Launch Diagnostic</Button>
            </div>
          </div>

          <div className="module-card api-mod" onClick={() => onSelectTab('api')}>
            <div className="module-header">
              <div className="module-icon-wrap api-badge">
                <IconLink className="w-6 h-6" />
              </div>
              <span className="module-badge">Contract Diff</span>
            </div>
            <h4>API Schema Drift Guard</h4>
            <p>Catch breaking payload discrepancies between microservices before production rollout with instant auto-fixers.</p>
            <div className="module-footer">
              <Button variant="secondary" size="compact">Inspect Contracts</Button>
            </div>
          </div>

          <div className="module-card predict-mod" onClick={() => onSelectTab('predict')}>
            <div className="module-header">
              <div className="module-icon-wrap predict-badge">
                <IconPredict className="w-6 h-6" />
              </div>
              <span className="module-badge">Predictive AI</span>
            </div>
            <h4>Failure Risk Radar</h4>
            <p>Correlate historical telemetry sequences to forecast cascading timeout storms and resource starvation.</p>
            <div className="module-footer">
              <Button variant="secondary" size="compact">Forecast Risk</Button>
            </div>
          </div>

          <div className="module-card rec-mod" onClick={() => onSelectTab('rec')}>
            <div className="module-header">
              <div className="module-icon-wrap rec-badge">
                <IconSparkles className="w-6 h-6" />
              </div>
              <span className="module-badge">Advisor</span>
            </div>
            <h4>Reliability Architecture</h4>
            <p>Synthesize production architecture recommendations across security, caching, load balancing, and database scaling.</p>
            <div className="module-footer">
              <Button variant="secondary" size="compact">Get Recommendations</Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}