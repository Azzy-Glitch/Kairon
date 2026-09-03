import React, { useMemo } from 'react';
import { AsyncView } from './StateViews';
import EmptyState from '../ui/EmptyState';
import GroupedBarChart from '../ui/charts/GroupedBarChart';
import HorizontalBarChart from '../ui/charts/HorizontalBarChart';
import PhaseAnnotatedLineChart from '../ui/charts/PhaseAnnotatedLineChart';
import { useIncidents } from '../../hooks/useIncidents';
import { IncidentStatus } from '../../types/incident';
import { IconPredict, IconShield, IconHistory } from '../Icons';

const MTTR_SERIES_KEYS = [{ dataKey: 'mttr', name: 'Mean time to resolve (min)' }];

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
            <IconPredict className="w-6 h-6 tone-accent" />
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

            <div className="analytics-block">
              <h4 className="subheading">Incidents by severity and service</h4>
              {stats.bySeverityAndService.length > 0 ? (
                <GroupedBarChart data={stats.bySeverityAndService} />
              ) : (
                <p className="panel-pending-text">No data yet.</p>
              )}
            </div>

            <div className="analytics-block">
              <h4 className="subheading">Mean time to resolve, by day</h4>
              {stats.mttrTrend.length >= 2 ? (
                <PhaseAnnotatedLineChart data={stats.mttrTrend} seriesKeys={MTTR_SERIES_KEYS} height={220} />
              ) : (
                <EmptyState
                  icon={<IconHistory className="w-10 h-10" />}
                  title="Not enough data for a trend yet"
                  description="This chart needs resolved incidents from at least two different days to plot a trend. Run the incident simulation on more than one day to populate it."
                />
              )}
            </div>

            {stats.byTitle.length > 0 && (
              <div className="analytics-block">
                <h4 className="subheading">Top recurring patterns</h4>
                <HorizontalBarChart data={stats.byTitle.map((e) => ({ label: e.label, value: e.count }))} color="var(--series-2)" />
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
    bySeverityAndService: countBySeverityAndService(incidents),
    byTitle: countBy(incidents, (i) => i.title).slice(0, 8),
    mttrTrend: mttrTrendByDay(resolved)
  };
}

/** "Incidents by severity + by service", one grouped chart rather than two separate lists
 * (redesign brief section 7's Analytics chart table). */
function countBySeverityAndService(incidents) {
  const byService = new Map();
  for (const incident of incidents) {
    const service = incident.service || 'Unknown';
    if (!byService.has(service)) byService.set(service, { label: service, Critical: 0, High: 0, Medium: 0, Low: 0 });
    const row = byService.get(service);
    const severity = incident.severity;
    if (severity && severity in row) row[severity] += 1;
  }
  return [...byService.values()].sort(
    (a, b) => b.Critical + b.High + b.Medium + b.Low - (a.Critical + a.High + a.Medium + a.Low)
  );
}

/** Mean time to resolve, trended by the day each incident was detected - not a single aggregate
 * number. Days with no resolution that day are simply absent, not zero-filled (a day nothing
 * resolved is not the same as a day everything resolved instantly). */
function mttrTrendByDay(resolvedIncidents) {
  const byDay = new Map();
  for (const incident of resolvedIncidents) {
    const minutes = (new Date(incident.updatedAt) - new Date(incident.detectedAt)) / 60000;
    if (!Number.isFinite(minutes) || minutes < 0) continue;

    const detected = new Date(incident.detectedAt);
    const isoDay = detected.toISOString().slice(0, 10);
    if (!byDay.has(isoDay)) {
      byDay.set(isoDay, { label: detected.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }), minutesList: [] });
    }
    byDay.get(isoDay).minutesList.push(minutes);
  }

  return [...byDay.entries()]
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([, { label, minutesList }]) => ({
      t: label,
      mttr: Math.round((minutesList.reduce((a, b) => a + b, 0) / minutesList.length) * 10) / 10
    }));
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
