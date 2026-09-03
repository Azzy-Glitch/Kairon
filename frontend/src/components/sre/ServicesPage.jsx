import React, { useMemo, useState } from 'react';
import { SeverityBadge, StatusBadge } from './Badges';
import { AsyncView } from './StateViews';
import Sparkline from './Sparkline';
import MetricTile from '../ui/MetricTile';
import Button from '../ui/Button';
import SourceFilterBar from '../ui/SourceFilterBar';
import { useFilteredBySource } from '../../lib/SourceFilterContext';
import { resolveSource, sourceMeta } from '../../lib/source';
import { domainForMetric } from '../../lib/chartDomains';
import { useIncidents } from '../../hooks/useIncidents';
import { useDemo } from '../../hooks/useDemo';
import { useServiceMetrics } from '../../hooks/useTelemetry';
import { relativeTime, formatDateTime, formatMetricValue, groupByService } from '../../services/incidentService';
import { IconServer, IconShield, IconChevronDown } from '../Icons';

const SOURCE_SECTION_ORDER = ['dotnet', 'python', 'unknown'];

function round(value) {
  if (value === null || value === undefined || Number.isNaN(value)) return null;
  return Math.abs(value) >= 100 ? Math.round(value) : Math.round(value * 10) / 10;
}

function toSeries(recent, pick) {
  return recent.map((s) => ({ t: new Date(s.timestamp).toLocaleTimeString(), v: round(pick(s)) }));
}

/**
 * Service-centric health view (frontend PRD section 24; redesign brief section 8: Services).
 *
 * Composed entirely from the existing incident list and demo telemetry - no new backend endpoint.
 * A "service" here is whatever the incident's own `service` field says; there is no separate
 * service registry to query, so a service with zero incidents ever is not something this page can
 * know about (frontend PRD section 28: do not fabricate data that is not available).
 *
 * Cards are grouped into one section per connected SDK source (brief section 8: "Card grid grouped
 * by source") using the same resolveSource heuristic the rest of this redesign uses - a service's
 * source is resolved from its own incidents (they all share one Application/Service name, so the
 * first incident's resolved source represents the whole group). Clicking a card opens a detail view
 * with full live metric tiles and that service's incident history (brief: "Clicking a service opens
 * a service detail view").
 */
export default function ServicesPage() {
  const feed = useIncidents({ status: '', pollMs: 5000 });
  const demo = useDemo({ pollMs: 5000 });
  const [selectedName, setSelectedName] = useState(null);

  const filteredIncidents = useFilteredBySource(feed.incidents, resolveSource);
  const services = useMemo(() => groupByService(filteredIncidents), [filteredIncidents]);

  const servicesBySource = useMemo(() => {
    const groups = { dotnet: [], python: [], unknown: [] };
    for (const svc of services) {
      const source = resolveSource(svc.incidents[0]);
      (groups[source] || groups.unknown).push(svc);
    }
    return groups;
  }, [services]);

  const selected = selectedName ? services.find((s) => s.name === selectedName) || null : null;

  if (selected) {
    return <ServiceDetail service={selected} onBack={() => setSelectedName(null)} />;
  }

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconServer className="w-6 h-6 tone-low" />
          </div>
          <div>
            <h3>Services</h3>
            <p className="section-desc">
              Reliability by service, derived from incidents Kairon has detected and correlated
            </p>
          </div>
        </div>
      </div>

      <SourceFilterBar records={feed.incidents} className="source-filter-bar" />

      <AsyncView
        query={feed}
        loadingLabel="Loading service health..."
        emptyTitle="No services observed yet"
        emptyHint="A service appears here once Kairon detects and correlates a signal against it. Run the incident simulation to see one."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() => (
          <>
            {SOURCE_SECTION_ORDER.filter((source) => servicesBySource[source].length > 0).map((source) => {
              const SourceIcon = sourceMeta[source].icon;
              return (
              <section key={source} className="services-source-section">
                <div className="services-source-heading">
                  <SourceIcon className="w-4 h-4" />
                  <span>{sourceMeta[source].label}</span>
                  <span className="services-source-count">{servicesBySource[source].length}</span>
                </div>
                <div className="services-grid">
                  {servicesBySource[source].map((svc) => (
                    <ServiceCard
                      key={svc.name}
                      service={svc}
                      source={source}
                      demoState={svc.name === 'OrderProcessingService' ? demo.data : null}
                      onOpen={() => setSelectedName(svc.name)}
                    />
                  ))}
                </div>
              </section>
              );
            })}
          </>
        )}
      </AsyncView>
    </div>
  );
}

function ServiceCard({ service, source, demoState, onOpen }) {
  const healthy = service.health === 'Healthy';
  const meta = sourceMeta[source];

  // Real recent samples for this service specifically (server-side filtered), not a fabricated or
  // system-wide trend. Renders nothing if the service has no recent metric rows.
  const metrics = useServiceMetrics(service.name);
  const cpuTrend = useMemo(
    () => [...(metrics.data || [])].reverse().map((m) => m.cpuPercent),
    [metrics.data]
  );

  return (
    <button
      type="button"
      className={`service-card service-card-clickable ${healthy ? 'service-healthy' : service.health === 'Critical' ? 'service-critical' : 'service-degraded'}`}
      onClick={onOpen}
    >
      <div className="service-card-head">
        <span className={`service-dot ${healthy ? 'good' : service.health === 'Critical' ? 'bad' : 'attention'}`} />
        <h4>{service.name}</h4>
        <span className="service-health-label">{service.health}</span>
      </div>

      <span className="service-card-source-chip" style={{ color: meta.tint, borderColor: meta.tint }}>
        <meta.icon className="w-3 h-3" />
        {meta.label}
      </span>

      {cpuTrend.length >= 2 && (
        <div className="service-card-trend">
          <span className="service-card-trend-label">CPU trend</span>
          <Sparkline values={cpuTrend} width={220} height={26} color={healthy ? 'var(--series-1)' : 'var(--critical)'} />
        </div>
      )}

      <div className="service-card-stats">
        <div>
          <span className="service-stat-label">Active incidents</span>
          <span className="service-stat-value">{service.activeCount}</span>
        </div>
        <div>
          <span className="service-stat-label">Total observed</span>
          <span className="service-stat-value">{service.incidents.length}</span>
        </div>
      </div>

      {demoState && (
        <div className="service-card-metrics">
          <span>CPU {formatMetricValue(demoState.cpuPercent, '%')}</span>
          <span>Latency {formatMetricValue(demoState.latencyMs, 'ms')}</span>
          <span>Errors {formatMetricValue(demoState.errorRate != null ? demoState.errorRate * 100 : null, '%')}</span>
        </div>
      )}

      {service.worstActive ? (
        <div className="service-card-incident">
          <SeverityBadge severity={service.worstActive.severity} size="sm" />
          <span className="service-card-incident-title">{service.worstActive.title}</span>
        </div>
      ) : (
        <p className="service-card-clean">No active incidents</p>
      )}

      {service.lastSeen && (
        <p className="service-card-lastseen">Last activity {relativeTime(service.lastSeen.updatedAt)}</p>
      )}
    </button>
  );
}

/** Service detail (redesign brief section 8: Services - "a service detail view" reached by
 * clicking a card): full live metric tiles for this one service plus its incident history. Metrics
 * come from the same server-side-scoped useServiceMetrics hook the card's sparkline already used -
 * just the fuller sample set rendered as MetricTiles instead of one sparkline. */
function ServiceDetail({ service, onBack }) {
  const source = resolveSource(service.incidents[0]);
  const meta = sourceMeta[source];
  const metrics = useServiceMetrics(service.name);
  const recent = useMemo(() => [...(metrics.data || [])].reverse(), [metrics.data]);
  const latest = recent[recent.length - 1] || null;

  const history = useMemo(
    () => [...service.incidents].sort((a, b) => new Date(b.detectedAt) - new Date(a.detectedAt)),
    [service.incidents]
  );

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <Button variant="ghost" size="compact" onClick={onBack} className="services-back-btn">
            <IconChevronDown className="w-4 h-4 services-back-icon" />
            Back to services
          </Button>
        </div>
      </div>

      <div className="service-detail-title-row">
        <span className={`service-dot ${service.health === 'Healthy' ? 'good' : service.health === 'Critical' ? 'bad' : 'attention'}`} />
        <h2>{service.name}</h2>
        <span className="service-card-source-chip" style={{ color: meta.tint, borderColor: meta.tint }}>
          <meta.icon className="w-3 h-3" />
          {meta.label}
        </span>
        <span className="service-health-label">{service.health}</span>
      </div>

      <div className="telemetry-snapshot-grid service-detail-metrics">
        <MetricTile
          label="CPU usage"
          value={round(latest?.cpuPercent)}
          unit="%"
          data={toSeries(recent, (s) => s.cpuPercent)}
          seriesColor="var(--series-1)"
          threshold={80}
          domain={domainForMetric('percent')}
        />
        <MetricTile
          label="Latency"
          value={round(latest?.responseTimeMs)}
          unit="ms"
          data={toSeries(recent, (s) => s.responseTimeMs)}
          seriesColor="var(--series-2)"
          threshold={1000}
          domain={domainForMetric('unbounded', { threshold: 1000, peak: latest?.responseTimeMs })}
        />
        <MetricTile
          label="Error rate"
          value={round(latest?.requestCount ? (latest.errorCount / latest.requestCount) * 100 : null)}
          unit="%"
          data={toSeries(recent, (s) => (s.requestCount ? (s.errorCount / s.requestCount) * 100 : null))}
          seriesColor="var(--series-3)"
          threshold={10}
          domain={domainForMetric('percent')}
        />
      </div>

      <div className="services-source-heading service-detail-history-heading">
        <span>Incident history</span>
        <span className="services-source-count">{history.length}</span>
      </div>

      {history.length === 0 ? (
        <p className="panel-pending-text">No incidents recorded for this service yet.</p>
      ) : (
        <ul className="service-detail-history-list">
          {history.map((incident) => (
            <li key={incident.id} className="service-detail-history-row">
              <SeverityBadge severity={incident.severity} size="sm" />
              <span className="service-detail-history-title">{incident.title}</span>
              <StatusBadge status={incident.status} />
              <span className="service-detail-history-time" title={formatDateTime(incident.detectedAt)}>
                {relativeTime(incident.detectedAt)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
