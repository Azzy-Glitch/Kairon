import React, { useEffect, useMemo, useState } from 'react';
import IncidentFeed from './IncidentFeed';
import IncidentDetail from './IncidentDetail';
import Sparkline from './Sparkline';
import { SeverityBadge, StatusBadge } from './Badges';
import { StaleBanner } from './StateViews';
import { useDashboard, useIncident, useIncidentActions, useIncidents, useRecentActivity } from '../../hooks/useIncidents';
import { formatMetricValue, groupByService, relativeTime } from '../../services/incidentService';
import { isTerminal } from '../../types/incident';
import { IconServer, IconShield, IconZap, IconBug, IconSparkles, IconHistory, IconDashboard } from '../Icons';

/**
 * The operator command view (frontend PRD sections 4 and 21).
 *
 * Priority order is the PRD's: active incidents first, then severity, then the detail needed to
 * decide. Raw telemetry stays available on its own screen rather than crowding this one.
 *
 * "Overview" and "Incidents" are deliberately one screen rather than two - see App.jsx's tab list
 * comment. This screen leads with a system-health summary (mirroring the reference layout a
 * teammate shared) and keeps the full incident feed + detail workspace below it, rather than
 * replacing that already-working, already-tested pane.
 */
export default function SreDashboard() {
  const [filters, setFilters] = useState({ status: 'active' });
  const [selectedId, setSelectedId] = useState(null);

  const dashboard = useDashboard();
  const feed = useIncidents({ status: filters.status });
  const detail = useIncident(selectedId);
  const activity = useRecentActivity({ limit: 12 });

  const actions = useIncidentActions(async () => {
    await Promise.all([
      detail.reload({ silent: true }),
      feed.reload({ silent: true }),
      dashboard.reload({ silent: true }),
      activity.reload({ silent: true })
    ]);
  });

  const summary = dashboard.data;
  const health = summary?.health;
  const overallHealthy = Boolean(health?.backend && health?.database && health?.aiService);
  const criticalCount = summary?.severityDistribution?.Critical || 0;

  // The most important active incident is selected by default, so the screen is useful the moment
  // it loads rather than after a click. Only applies while nothing is selected, so a poll never
  // yanks the operator away from the incident they are reading.
  useEffect(() => {
    if (selectedId) return;

    const candidate = summary?.topIncident?.id || feed.incidents[0]?.id;
    if (candidate) setSelectedId(candidate);
  }, [selectedId, summary?.topIncident?.id, feed.incidents]);

  // Both derived from the active feed already being fetched for the list pane - no extra request.
  const topIncidents = useMemo(
    () => feed.incidents.filter((i) => !isTerminal(i.status)).slice(0, 3),
    [feed.incidents]
  );
  const services = useMemo(() => groupByService(feed.incidents), [feed.incidents]);

  return (
    <div className="sre-dashboard animate-fade-in">
      <StaleBanner error={dashboard.data && dashboard.isError ? dashboard.error : null} />

      <div className="sre-primary-grid">
        <PrimaryStatCard
          icon={<IconShield className="w-5 h-5" />}
          label="Overall health"
          value={overallHealthy ? 'Healthy' : 'Degraded'}
          sub={overallHealthy ? 'All systems go' : 'A subsystem needs attention'}
          tone={overallHealthy ? 'good' : 'urgent'}
        />
        <PrimaryStatCard
          icon={<IconDashboard className="w-5 h-5" />}
          label="Active services"
          value={summary?.activeServiceCount ?? '--'}
          sub="With an active incident"
          tone="neutral"
        />
        <PrimaryStatCard
          icon={<IconBug className="w-5 h-5" />}
          label="Active incidents"
          value={summary?.activeIncidents ?? '--'}
          sub={summary?.activeIncidents > 0 ? 'See feed below' : 'None open'}
          tone={summary?.activeIncidents > 0 ? 'attention' : 'good'}
        />
        <PrimaryStatCard
          icon={<IconZap className="w-5 h-5" />}
          label="Critical"
          value={criticalCount}
          sub={criticalCount > 0 ? 'Needs attention' : 'No critical incidents'}
          tone={criticalCount > 0 ? 'urgent' : 'good'}
        />
        <PrimaryStatCard
          icon={<IconSparkles className="w-5 h-5" />}
          label="AI service"
          value={health?.aiService ? 'Operational' : 'Offline'}
          sub={health?.aiMode ? `Provider: ${health.aiMode}` : ' '}
          tone={health?.aiService ? 'good' : 'urgent'}
        />
        <PrimaryStatCard
          icon={<IconServer className="w-5 h-5" />}
          label="Detection"
          value={health?.detectionEnabled ? 'Active' : 'Paused'}
          sub={health?.detectionEnabled ? 'Live telemetry' : 'Not evaluating'}
          tone={health?.detectionEnabled ? 'good' : 'attention'}
        />
      </div>

      <div className="sre-summary-grid">
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

      <TelemetrySnapshot metrics={summary?.metrics} />

      {topIncidents.length > 0 && (
        <section className="active-incidents-section">
          <div className="active-incidents-head">
            <span className="block-label">Active incidents</span>
          </div>
          <div className="active-incidents-grid">
            {topIncidents.map((incident) => (
              <ActiveIncidentCard
                key={incident.id}
                incident={incident}
                selected={incident.id === selectedId}
                onOpen={() => setSelectedId(incident.id)}
              />
            ))}
          </div>
        </section>
      )}

      {services.length > 0 && <ServicesHealthTable services={services} />}

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

      <RecentActivityPanel activity={activity} />

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

function PrimaryStatCard({ icon, label, value, sub, tone = 'neutral' }) {
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

/** Live metrics with a real trend line behind each reading, built from the same sampled telemetry
 * the detection engine evaluates - not a fabricated or decorative series. */
function TelemetrySnapshot({ metrics }) {
  const recent = metrics?.recent || [];

  const series = {
    cpu: recent.map((s) => s.cpuPercent),
    latency: recent.map((s) => s.responseTimeMs),
    errorRate: recent.map((s) => (s.requestCount ? (s.errorCount / s.requestCount) * 100 : null)),
    retries: recent.map((s) => s.retryCount),
    queue: recent.map((s) => s.queueDepth)
  };

  return (
    <div className="telemetry-snapshot">
      <div className="telemetry-snapshot-head">
        <span className="block-label">Telemetry snapshot</span>
        <span className="telemetry-snapshot-time">
          {metrics?.sampledAt ? `sampled ${relativeTime(metrics.sampledAt)}` : 'no telemetry yet'}
        </span>
      </div>

      <div className="telemetry-snapshot-grid">
        <TelemetryCard label="CPU usage" value={formatMetricValue(metrics?.cpuPercent, '%')} values={series.cpu} color="#38bdf8" breaching={metrics?.cpuPercent > 80} />
        <TelemetryCard label="Latency (p95)" value={formatMetricValue(metrics?.latencyMs, 'ms')} values={series.latency} color="#818cf8" breaching={metrics?.latencyMs > 1000} />
        <TelemetryCard
          label="Error rate"
          value={formatMetricValue(metrics?.errorRate != null ? metrics.errorRate * 100 : null, '%')}
          values={series.errorRate}
          color="#fb7185"
          breaching={metrics?.errorRate > 0.1}
        />
        <TelemetryCard label="Retry rate" value={formatMetricValue(metrics?.retriesPerMinute, '/min')} values={series.retries} color="#fbbf24" breaching={metrics?.retriesPerMinute > 30} />
        <TelemetryCard label="Queue depth" value={formatMetricValue(metrics?.queueDepth, '')} values={series.queue} color="#a78bfa" breaching={metrics?.queueDepth > 50} />
      </div>
    </div>
  );
}

function TelemetryCard({ label, value, values, color, breaching }) {
  return (
    <div className={`telemetry-card ${breaching ? 'breaching' : ''}`}>
      <span className="telemetry-card-label">{label}</span>
      <span className="telemetry-card-value">{value}</span>
      <div className="telemetry-card-chart">
        <Sparkline values={values} width={140} height={32} color={breaching ? '#fb7185' : color} />
      </div>
    </div>
  );
}

function ActiveIncidentCard({ incident, selected, onOpen }) {
  return (
    <button type="button" className={`active-incident-card ${selected ? 'selected' : ''}`} onClick={onOpen}>
      <span className={`incident-severity-rail sev-rail-${(incident.severity || '').toLowerCase()}`} />
      <div className="active-incident-body">
        <div className="active-incident-head">
          <span className="incident-key">{incident.incidentKey}</span>
          <SeverityBadge severity={incident.severity} size="sm" />
        </div>
        <div className="active-incident-title">{incident.title}</div>
        <div className="active-incident-meta">
          <StatusBadge status={incident.status} />
          <span className="active-incident-service">{incident.service}</span>
          <span className="active-incident-time">Detected {relativeTime(incident.detectedAt)}</span>
        </div>
      </div>
    </button>
  );
}

/** Compact services table, scoped to services with an active incident right now - the fuller,
 * all-time services view lives on its own Services page. */
function ServicesHealthTable({ services }) {
  return (
    <section className="services-health-section">
      <div className="services-health-head">
        <span className="block-label">Services with active incidents</span>
      </div>
      <div className="table-responsive">
        <table className="custom-table services-health-table">
          <thead>
            <tr>
              <th>Service</th>
              <th>Health</th>
              <th>Active</th>
              <th>Total observed</th>
              <th>Last activity</th>
            </tr>
          </thead>
          <tbody>
            {services.map((svc) => (
              <tr key={svc.name}>
                <td>{svc.name}</td>
                <td>
                  <span className={`service-dot ${svc.health === 'Healthy' ? 'good' : svc.health === 'Critical' ? 'bad' : 'attention'}`} />
                  {svc.health}
                </td>
                <td>{svc.activeCount}</td>
                <td>{svc.incidents.length}</td>
                <td>{svc.lastSeen ? relativeTime(svc.lastSeen.updatedAt) : '--'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function RecentActivityPanel({ activity }) {
  const events = activity.data || [];
  if (!activity.isLoading && events.length === 0) return null;

  return (
    <section className="recent-activity-section">
      <div className="recent-activity-head">
        <IconHistory className="w-4 h-4" />
        <span className="block-label">Recent activity</span>
      </div>
      <ul className="recent-activity-list">
        {events.map((event) => (
          <li key={event.id} className="recent-activity-row">
            <span className={`recent-activity-dot dot-${eventTone(event.eventType)}`} />
            <span className="recent-activity-key">{event.incidentKey}</span>
            <span className="recent-activity-text">{activityText(event)}</span>
            <span className="recent-activity-time">{relativeTime(event.timestamp)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

function activityText(event) {
  if (event.message) return event.message;
  if (event.newState) return `${event.eventType} - ${event.newState}`;
  return event.eventType;
}

function eventTone(eventType) {
  const type = (eventType || '').toLowerCase();
  if (type.includes('fail') || type.includes('reject')) return 'bad';
  if (type.includes('resolv') || type.includes('approv') || type.includes('execut')) return 'good';
  if (type.includes('detect') || type.includes('correlat')) return 'attention';
  return 'neutral';
}
