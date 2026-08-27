import React, { useMemo } from 'react';
import { SeverityBadge } from './Badges';
import { AsyncView } from './StateViews';
import { useIncidents } from '../../hooks/useIncidents';
import { useDemo } from '../../hooks/useDemo';
import { relativeTime, formatMetricValue, groupByService } from '../../services/incidentService';
import { IconServer, IconShield } from '../Icons';

/**
 * Service-centric health view (frontend PRD section 24).
 *
 * Composed entirely from the existing incident list and demo telemetry - no new backend endpoint.
 * A "service" here is whatever the incident's own `service` field says; there is no separate
 * service registry to query, so a service with zero incidents ever is not something this page can
 * know about (frontend PRD section 28: do not fabricate data that is not available).
 */
export default function ServicesPage() {
  const feed = useIncidents({ status: '', pollMs: 5000 });
  const demo = useDemo({ pollMs: 5000 });

  const services = useMemo(() => groupByService(feed.incidents), [feed.incidents]);

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconServer className="w-6 h-6 text-sky-500" />
          </div>
          <div>
            <h3>Services</h3>
            <p className="section-desc">
              Reliability by service, derived from incidents Kairon has detected and correlated
            </p>
          </div>
        </div>
      </div>

      <AsyncView
        query={feed}
        loadingLabel="Loading service health..."
        emptyTitle="No services observed yet"
        emptyHint="A service appears here once Kairon detects and correlates a signal against it. Run the incident simulation to see one."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() => (
          <div className="services-grid">
            {services.map((svc) => (
              <ServiceCard key={svc.name} service={svc} demoState={svc.name === 'OrderProcessingService' ? demo.data : null} />
            ))}
          </div>
        )}
      </AsyncView>
    </div>
  );
}

function ServiceCard({ service, demoState }) {
  const healthy = service.health === 'Healthy';

  return (
    <div className={`service-card ${healthy ? 'service-healthy' : service.health === 'Critical' ? 'service-critical' : 'service-degraded'}`}>
      <div className="service-card-head">
        <span className={`service-dot ${healthy ? 'good' : service.health === 'Critical' ? 'bad' : 'attention'}`} />
        <h4>{service.name}</h4>
        <span className="service-health-label">{service.health}</span>
      </div>

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
    </div>
  );
}
