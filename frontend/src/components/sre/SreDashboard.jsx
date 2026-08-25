import React, { useEffect, useState } from 'react';
import IncidentFeed from './IncidentFeed';
import IncidentDetail from './IncidentDetail';
import { SeverityBadge, StatusBadge } from './Badges';
import { StaleBanner } from './StateViews';
import { useDashboard, useIncident, useIncidentActions, useIncidents } from '../../hooks/useIncidents';
import { formatMetricValue, relativeTime } from '../../services/incidentService';
import { IconServer, IconShield, IconZap, IconBug } from '../Icons';

/**
 * The operator command view (frontend PRD sections 4 and 21).
 *
 * Priority order is the PRD's: active incidents first, then severity, then the detail needed to
 * decide. Raw telemetry stays available on its own screen rather than crowding this one.
 */
export default function SreDashboard() {
  const [filters, setFilters] = useState({ status: 'active' });
  const [selectedId, setSelectedId] = useState(null);

  const dashboard = useDashboard();
  const feed = useIncidents({ status: filters.status });
  const detail = useIncident(selectedId);

  const actions = useIncidentActions(async () => {
    await Promise.all([detail.reload({ silent: true }), feed.reload({ silent: true }), dashboard.reload({ silent: true })]);
  });

  const summary = dashboard.data;

  // The most important active incident is selected by default, so the screen is useful the moment
  // it loads rather than after a click. Only applies while nothing is selected, so a poll never
  // yanks the operator away from the incident they are reading.
  useEffect(() => {
    if (selectedId) return;

    const candidate = summary?.topIncident?.id || feed.incidents[0]?.id;
    if (candidate) setSelectedId(candidate);
  }, [selectedId, summary?.topIncident?.id, feed.incidents]);

  return (
    <div className="sre-dashboard animate-fade-in">
      <StaleBanner error={dashboard.data && dashboard.isError ? dashboard.error : null} />

      <div className="sre-summary-grid">
        <SummaryTile
          icon={<IconBug className="w-5 h-5" />}
          label="Active incidents"
          value={summary?.activeIncidents ?? '--'}
          tone={summary?.activeIncidents > 0 ? 'attention' : 'good'}
        />
        <SummaryTile
          icon={<IconShield className="w-5 h-5" />}
          label="Awaiting approval"
          value={summary?.awaitingApproval ?? '--'}
          tone={summary?.awaitingApproval > 0 ? 'urgent' : 'neutral'}
          sub={summary?.awaitingApproval > 0 ? 'Operator decision needed' : 'Nothing pending'}
        />
        <SummaryTile
          icon={<IconZap className="w-5 h-5" />}
          label="Remediating"
          value={summary?.remediating ?? '--'}
          tone="active"
        />
        <SummaryTile
          icon={<IconServer className="w-5 h-5" />}
          label="Resolved (24h)"
          value={summary?.resolvedLast24h ?? '--'}
          tone="good"
        />
      </div>

      <div className="sre-metrics-strip">
        <LiveMetric label="CPU" value={summary?.metrics?.cpuPercent} unit="%" threshold={80} />
        <LiveMetric label="Memory" value={summary?.metrics?.memoryPercent} unit="%" threshold={85} />
        <LiveMetric label="Latency" value={summary?.metrics?.latencyMs} unit="ms" threshold={1000} />
        <LiveMetric
          label="Error rate"
          value={summary?.metrics?.errorRate != null ? summary.metrics.errorRate * 100 : null}
          unit="%"
          threshold={10}
        />
        <LiveMetric label="Retries" value={summary?.metrics?.retriesPerMinute} unit="/min" threshold={30} />
        <LiveMetric label="Queue" value={summary?.metrics?.queueDepth} unit="" threshold={50} />

        <span className="metrics-timestamp">
          {summary?.metrics?.sampledAt ? `sampled ${relativeTime(summary.metrics.sampledAt)}` : 'no telemetry yet'}
        </span>
      </div>

      {summary?.topIncident && (
        <TopIncidentBanner incident={summary.topIncident} onOpen={() => setSelectedId(summary.topIncident.id)} />
      )}

      <div className="sre-workspace">
        <IncidentFeed
          query={feed}
          incidents={feed.incidents}
          selectedId={selectedId}
          onSelect={setSelectedId}
          filters={filters}
          onFilterChange={setFilters}
        />

        <div className="sre-detail-pane">
          <IncidentDetail query={detail} actions={actions} />
        </div>
      </div>

      {summary?.severityDistribution && Object.keys(summary.severityDistribution).length > 0 && (
        <div className="severity-distribution">
          <span className="block-label">Active severity distribution</span>
          <div className="severity-bars">
            {Object.entries(summary.severityDistribution).map(([severity, count]) => (
              <div key={severity} className="severity-bar-row">
                <SeverityBadge severity={severity} size="sm" />
                <div className="severity-bar-track">
                  <div
                    className={`severity-bar-fill sev-fill-${severity.toLowerCase()}`}
                    style={{ width: `${Math.min(100, count * 20)}%` }}
                  />
                </div>
                <span className="severity-bar-count">{count}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function SummaryTile({ icon, label, value, sub, tone = 'neutral' }) {
  return (
    <div className={`sre-tile tile-${tone}`}>
      <span className="sre-tile-icon">{icon}</span>
      <div className="sre-tile-body">
        <span className="sre-tile-label">{label}</span>
        <span className="sre-tile-value">{value}</span>
        {sub && <span className="sre-tile-sub">{sub}</span>}
      </div>
    </div>
  );
}

/** A live metric reading, coloured against the same threshold the detection engine uses. */
function LiveMetric({ label, value, unit, threshold }) {
  const breaching = value !== null && value !== undefined && threshold && value > threshold;

  return (
    <div className={`live-metric ${breaching ? 'breaching' : ''}`}>
      <span className="live-metric-label">{label}</span>
      <span className="live-metric-value">{formatMetricValue(value, unit)}</span>
      {threshold && <span className="live-metric-threshold">/ {threshold}{unit}</span>}
    </div>
  );
}

function TopIncidentBanner({ incident, onOpen }) {
  return (
    <button type="button" className="top-incident-banner" onClick={onOpen}>
      <span className={`incident-severity-rail sev-rail-${(incident.severity || '').toLowerCase()}`} />

      <div className="top-incident-body">
        <div className="top-incident-head">
          <span className="incident-key">{incident.incidentKey}</span>
          <SeverityBadge severity={incident.severity} size="sm" />
          <StatusBadge status={incident.status} />
          <span className="top-incident-tag">Most important active incident</span>
        </div>
        <div className="top-incident-title">{incident.title}</div>
        <div className="top-incident-desc">{incident.shortDescription}</div>
      </div>
    </button>
  );
}
