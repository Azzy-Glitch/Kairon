import React, { useMemo } from 'react';
import { AsyncView } from './StateViews';
import { useIncidents } from '../../hooks/useIncidents';
import { IncidentStatus, SEVERITY_ORDER } from '../../types/incident';
import { IconPredict, IconShield } from '../Icons';

/**
 * Operational analytics (frontend PRD section 28).
 *
 * Computed only from what the incident list endpoint already returns. Mean-time-to-detect is
 * deliberately not shown: it would require knowing when the underlying failure began, which is not
 * data this frontend has - showing a number for it would be fabricating a metric (frontend PRD
 * section 28's explicit "do not fabricate metrics when backend data is unavailable").
 */
export default function AnalyticsPage() {
  const feed = useIncidents({ status: '', pollMs: 15000 });

  const stats = useMemo(() => computeStats(feed.incidents), [feed.incidents]);

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconPredict className="w-6 h-6 text-purple-500" />
          </div>
          <div>
            <h3>Analytics</h3>
            <p className="section-desc">Operational trends across every incident Kairon has recorded</p>
          </div>
        </div>
      </div>

      <AsyncView
        query={feed}
        loadingLabel="Loading analytics..."
        emptyTitle="Not enough data yet"
        emptyHint="Analytics build up as incidents are recorded. Run the incident simulation to generate some."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() => (
          <>
            <div className="analytics-summary-grid">
              <AnalyticsTile label="Total incidents" value={stats.total} />
              <AnalyticsTile label="Resolved" value={stats.resolved} tone="good" />
              <AnalyticsTile label="Failed / rejected" value={stats.failed} tone={stats.failed > 0 ? 'bad' : 'neutral'} />
              <AnalyticsTile
                label="Mean time to resolve"
                value={stats.mttrMinutes !== null ? `${stats.mttrMinutes}m` : 'not enough data'}
                tone="neutral"
              />
            </div>

            <div className="analytics-columns">
              <div className="analytics-block">
                <h4 className="subheading">By severity</h4>
                <BarList entries={stats.bySeverity} />
              </div>

              <div className="analytics-block">
                <h4 className="subheading">By service</h4>
                <BarList entries={stats.byService} />
              </div>
            </div>

            {stats.byTitle.length > 0 && (
              <div className="analytics-block">
                <h4 className="subheading">Top recurring patterns</h4>
                <BarList entries={stats.byTitle} />
              </div>
            )}
          </>
        )}
      </AsyncView>
    </div>
  );
}

function computeStats(incidents = []) {
  const total = incidents.length;
  const resolved = incidents.filter((i) => i.status === IncidentStatus.Resolved);
  const failed = incidents.filter((i) =>
    [IncidentStatus.Failed, IncidentStatus.Rejected].includes(i.status)
  ).length;

  const resolveTimes = resolved
    .map((i) => (new Date(i.updatedAt) - new Date(i.detectedAt)) / 60000)
    .filter((minutes) => Number.isFinite(minutes) && minutes >= 0);

  const mttrMinutes =
    resolveTimes.length > 0 ? Math.round((resolveTimes.reduce((a, b) => a + b, 0) / resolveTimes.length) * 10) / 10 : null;

  return {
    total,
    resolved: resolved.length,
    failed,
    mttrMinutes,
    bySeverity: countBy(incidents, (i) => i.severity, SEVERITY_ORDER),
    byService: countBy(incidents, (i) => i.service || 'Unknown'),
    byTitle: countBy(incidents, (i) => i.title).slice(0, 8)
  };
}

function countBy(items, keyFn, order) {
  const map = new Map();
  for (const item of items) {
    const key = keyFn(item) || 'Unknown';
    map.set(key, (map.get(key) || 0) + 1);
  }

  const entries = [...map.entries()];
  if (order) {
    entries.sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]));
  } else {
    entries.sort((a, b) => b[1] - a[1]);
  }
  return entries.map(([label, count]) => ({ label, count }));
}

function AnalyticsTile({ label, value, tone = 'neutral' }) {
  return (
    <div className={`sre-tile tile-${tone}`}>
      <div className="sre-tile-body">
        <span className="sre-tile-label">{label}</span>
        <span className="sre-tile-value">{value}</span>
      </div>
    </div>
  );
}

function BarList({ entries }) {
  if (!entries.length) return <p className="panel-pending-text">No data yet.</p>;
  const max = Math.max(...entries.map((e) => e.count), 1);

  return (
    <div className="analytics-bar-list">
      {entries.map((entry) => (
        <div key={entry.label} className="analytics-bar-row">
          <span className="analytics-bar-label">{entry.label}</span>
          <div className="analytics-bar-track">
            <div className="analytics-bar-fill" style={{ width: `${(entry.count / max) * 100}%` }} />
          </div>
          <span className="analytics-bar-count">{entry.count}</span>
        </div>
      ))}
    </div>
  );
}
