import React, { useMemo } from 'react';
import MetricTile from '../ui/MetricTile';
import { SeverityBadge, StatusBadge } from './Badges';
import { StaleBanner } from './StateViews';
import SourceFilterBar from '../ui/SourceFilterBar';
import { useFilteredBySource } from '../../lib/SourceFilterContext';
import { resolveSource } from '../../lib/source';
import { SEVERITY_ORDER } from '../../lib/labels';
import { domainForMetric } from '../../lib/chartDomains';
import { useDashboard, useIncidents, useRecentActivity } from '../../hooks/useIncidents';
import { groupByService, relativeTime } from '../../services/incidentService';
import { formatAbsoluteTime } from '../../lib/labels';
import { isTerminal } from '../../types/incident';
import { IconServer, IconShield, IconZap, IconBug, IconSparkles, IconHistory } from '../Icons';

/**
 * Overview (Kairon frontend redesign brief, section 8): a real dashboard - four key live numbers,
 * a telemetry snapshot, and a short "needs attention" glance list that links to the standalone
 * Incidents page rather than re-rendering its full feed here. The full filterable incident list and
 * detail workspace live on their own page (IncidentsPage) so an incident never appears fully twice.
 */
export default function SreDashboard({ onOpenIncidents }) {
  const dashboard = useDashboard();
  const feed = useIncidents({ status: 'active' });
  const activity = useRecentActivity({ limit: 12 });

  const summary = dashboard.data;
  const health = summary?.health;
  const criticalCount = summary?.severityDistribution?.Critical || 0;
  const highCount = summary?.severityDistribution?.High || 0;
  // Must never read "Healthy" while a critical/high incident is open, even if every subsystem is
  // technically up - a contradicted health pill destroys operator trust (brief section 3).
  const componentsHealthy = Boolean(health?.backend && health?.database && health?.aiService);
  const overallHealthy = componentsHealthy && criticalCount === 0 && highCount === 0;

  const filteredIncidents = useFilteredBySource(feed.incidents, resolveSource);

  // Derived from the active feed already being fetched for the glance list - no extra request.
  const topIncidents = useMemo(
    () => filteredIncidents.filter((i) => !isTerminal(i.status)).slice(0, 3),
    [filteredIncidents]
  );
  const services = useMemo(() => groupByService(filteredIncidents), [filteredIncidents]);

  return (
    <div className="sre-dashboard animate-fade-in">
      <StaleBanner error={dashboard.data && dashboard.isError ? dashboard.error : null} />
      <SourceFilterBar records={feed.incidents} className="source-filter-bar" />

      <div className="sre-primary-grid">
        <PrimaryStatCard
          icon={<IconShield className="w-5 h-5" />}
          label="Overall health"
          value={overallHealthy ? 'Healthy' : 'Degraded'}
          sub={
            !componentsHealthy
              ? 'A subsystem needs attention'
              : criticalCount > 0
                ? `${criticalCount} critical incident(s) open`
                : highCount > 0
                  ? `${highCount} high-severity incident(s) open`
                  : 'All systems go'
          }
          tone={overallHealthy ? 'good' : 'urgent'}
        />
        <PrimaryStatCard
          icon={<IconBug className="w-5 h-5" />}
          label="Active incidents"
          value={summary?.activeIncidents ?? '--'}
          sub={`${summary?.activeServiceCount ?? 0} service(s) affected`}
          tone={summary?.activeIncidents > 0 ? 'attention' : 'good'}
        />
        <PrimaryStatCard
          icon={<IconZap className="w-5 h-5" />}
          label="Needs your attention"
          value={summary?.awaitingApproval ?? '--'}
          sub={
            criticalCount > 0
              ? `${criticalCount} critical open`
              : summary?.awaitingApproval > 0
                ? 'Awaiting approval'
                : 'Nothing pending'
          }
          tone={summary?.awaitingApproval > 0 || criticalCount > 0 ? 'urgent' : 'good'}
        />
        <PrimaryStatCard
          icon={<IconSparkles className="w-5 h-5" />}
          label="AI service"
          value={health?.aiService ? 'Operational' : 'Offline'}
          sub={health?.aiMode ? `Provider: ${health.aiMode}` : ' '}
          tone={health?.aiService ? 'good' : 'urgent'}
        />
      </div>

      <TelemetrySnapshot metrics={summary?.metrics} />

      {topIncidents.length > 0 && (
        <section className="active-incidents-section">
          <div className="active-incidents-head">
            <span className="block-label">Needs attention</span>
            {onOpenIncidents && (
              <button type="button" className="secondary-btn" onClick={onOpenIncidents}>
                View all incidents
              </button>
            )}
          </div>
          <div className="active-incidents-grid">
            {topIncidents.map((incident) => (
              <ActiveIncidentCard key={incident.id} incident={incident} onOpen={onOpenIncidents} />
            ))}
          </div>
        </section>
      )}

      {services.length > 0 && <ServicesHealthTable services={services} />}

      <RecentActivityPanel activity={activity} />

      {summary?.severityDistribution && Object.keys(summary.severityDistribution).length > 0 && (
        <div className="severity-distribution">
          <span className="block-label">Active severity distribution</span>
          <div className="severity-bars">
            {SEVERITY_ORDER.filter((severity) => summary.severityDistribution[severity] > 0).map((severity) => {
              const count = summary.severityDistribution[severity];
              return (
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
              );
            })}
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

function round(value) {
  if (value === null || value === undefined || Number.isNaN(value)) return null;
  return Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
}

function toSeries(recent, pick) {
  return recent.map((s) => ({ t: new Date(s.timestamp).toLocaleTimeString(), v: round(pick(s)) }));
}

/**
 * Live metrics (redesign brief section 7): a real MetricTile per metric - area chart, dashed
 * threshold line, breach-colored value text - fed from the same sampled telemetry the detection
 * engine evaluates, not a fabricated or decorative series. The chart line itself always keeps its
 * own series color even while breaching, per MetricTile's own "threshold as a line, not a colour"
 * rule; only the headline number and tile border switch to critical.
 */
function TelemetrySnapshot({ metrics }) {
  const recent = metrics?.recent || [];

  const errorRatePercent = metrics?.errorRate != null ? metrics.errorRate * 100 : null;

  return (
    <div className="telemetry-snapshot">
      <div className="telemetry-snapshot-head">
        <span className="block-label">Telemetry snapshot</span>
        <span
          className="telemetry-snapshot-time"
          title={metrics?.sampledAt ? formatAbsoluteTime(metrics.sampledAt) : undefined}
        >
          {metrics?.sampledAt ? `sampled ${relativeTime(metrics.sampledAt)}` : 'no telemetry yet'}
        </span>
      </div>

      <div className="telemetry-snapshot-grid">
        <MetricTile
          label="CPU usage"
          value={round(metrics?.cpuPercent)}
          unit="%"
          data={toSeries(recent, (s) => s.cpuPercent)}
          seriesColor="var(--series-1)"
          threshold={80}
          domain={domainForMetric('percent')}
        />
        <MetricTile
          label="Latency (p95)"
          value={round(metrics?.latencyMs)}
          unit="ms"
          data={toSeries(recent, (s) => s.responseTimeMs)}
          seriesColor="var(--series-2)"
          threshold={1000}
          domain={domainForMetric('unbounded', { threshold: 1000, peak: metrics?.latencyMs })}
        />
        <MetricTile
          label="Error rate"
          value={round(errorRatePercent)}
          unit="%"
          data={toSeries(recent, (s) => (s.requestCount ? (s.errorCount / s.requestCount) * 100 : null))}
          seriesColor="var(--series-3)"
          threshold={10}
          domain={domainForMetric('percent')}
        />
        <MetricTile
          label="Retry rate"
          value={round(metrics?.retriesPerMinute)}
          unit="/min"
          data={toSeries(recent, (s) => s.retryCount)}
          seriesColor="var(--series-5)"
          threshold={30}
          domain={domainForMetric('unbounded', { threshold: 30, peak: metrics?.retriesPerMinute })}
        />
        <MetricTile
          label="Queue depth"
          value={round(metrics?.queueDepth)}
          data={toSeries(recent, (s) => s.queueDepth)}
          seriesColor="var(--series-4)"
          threshold={50}
          domain={domainForMetric('unbounded', { threshold: 50, peak: metrics?.queueDepth })}
        />
      </div>
    </div>
  );
}

function ActiveIncidentCard({ incident, onOpen }) {
  return (
    <button type="button" className="active-incident-card" onClick={onOpen}>

      <span className={`incident-severity-rail sev-rail-${(incident.severity || '').toLowerCase()}`} />
      <div className="active-incident-body">
        <div className="active-incident-head">
          <span className="incident-key">{incident.incidentKey}</span>
          <SeverityBadge severity={incident.severity} size="sm" />
        </div>
        <div className="active-incident-title">{incident.title}</div>
        <div className="active-incident-meta">
          <StatusBadge status={incident.status} />
          {incident.service && !incident.title?.includes(incident.service) && (
            <span className="active-incident-service">{incident.service}</span>
          )}
          <span className="active-incident-time" title={formatAbsoluteTime(incident.detectedAt)}>
            Detected {relativeTime(incident.detectedAt)}
          </span>
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
                <td title={svc.lastSeen ? formatAbsoluteTime(svc.lastSeen.updatedAt) : undefined}>
                  {svc.lastSeen ? relativeTime(svc.lastSeen.updatedAt) : '--'}
                </td>
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
            <span className="recent-activity-time" title={formatAbsoluteTime(event.timestamp)}>
              {relativeTime(event.timestamp)}
            </span>
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
