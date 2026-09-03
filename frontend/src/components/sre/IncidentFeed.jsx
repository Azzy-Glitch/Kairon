import React from 'react';
import { SeverityBadge, StatusBadge } from './Badges';
import { AsyncView } from './StateViews';
import EmptyState from '../ui/EmptyState';
import { relativeTime } from '../../services/incidentService';
import { formatAbsoluteTime } from '../../lib/labels';
import { IncidentStatus } from '../../types/incident';
import { IconShield } from '../Icons';

/**
 * The live incident feed (frontend PRD section 5) - the "card view" option on the Incidents page.
 * Status/severity/service/time-range filtering lives one level up in IncidentsPage.jsx, shared with
 * the table view, so `incidents` here is already the fully-filtered set.
 *
 * Refreshed by polling rather than WebSockets: the backend has no push channel, and the PRD
 * explicitly permits polling for the hackathon provided it is efficient. The poll is silent and
 * pauses in a hidden tab, so the list never flickers and a backgrounded dashboard costs nothing.
 */
export default function IncidentFeed({ query, incidents, selectedId, onSelect }) {
  return (
    <div className="incident-feed">
      <div className="incident-feed-header">
        <div>
          <h3>Incident Feed</h3>
          <p className="section-desc">
            {incidents.length} {incidents.length === 1 ? 'incident' : 'incidents'}
            {query.lastUpdated ? (
              <span title={formatAbsoluteTime(query.lastUpdated)}> - updated {relativeTime(query.lastUpdated)}</span>
            ) : ''}
          </p>
        </div>
      </div>

      <AsyncView
        query={query}
        loadingLabel="Loading incidents..."
        emptyTitle="No incidents"
        emptyHint="Nothing is currently degraded. Start the demo scenario to see the pipeline run."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() =>
          incidents.length === 0 ? (
            <EmptyState
              icon={<IconShield className="w-10 h-10" />}
              title="No matching incidents"
              description="Try widening the filters above."
            />
          ) : (
          <ul className="incident-list">
            {incidents.map((incident) => (
              <li key={incident.id}>
                <button
                  type="button"
                  className={`incident-row ${selectedId === incident.id ? 'selected' : ''} ${
                    incident.status === IncidentStatus.AwaitingApproval ? 'needs-approval' : ''
                  }`}
                  onClick={() => onSelect(incident.id)}
                >
                  <span className={`incident-severity-rail sev-rail-${(incident.severity || '').toLowerCase()}`} />

                  <div className="incident-row-body">
                    <div className="incident-row-top">
                      <span className="incident-key">{incident.incidentKey}</span>
                      <SeverityBadge severity={incident.severity} size="sm" />
                      <StatusBadge status={incident.status} />
                    </div>

                    <div className="incident-row-title">{incident.title}</div>

                    <div className="incident-row-meta">
                      {/* Most incident titles already open with the service name (e.g. "X Service
                          Degradation") - repeating it here would be the exact "duplicate badges/
                          info" bug the redesign brief calls out, so it only shows when the title
                          doesn't already say it. */}
                      {incident.service && !incident.title?.includes(incident.service) && (
                        <span>{incident.service}</span>
                      )}
                      {incident.affectedComponent && incident.affectedComponent !== incident.affectedEndpoint && (
                        <span>{incident.affectedComponent}</span>
                      )}
                      <span title={formatAbsoluteTime(incident.detectedAt)}>{relativeTime(incident.detectedAt)}</span>
                    </div>

                    {incident.shortDescription && (
                      <div className="incident-row-desc">{incident.shortDescription}</div>
                    )}
                  </div>

                  {/* The single most actionable state gets its own marker in the list. */}
                  {incident.needsApproval && <span className="approval-flag">Approval needed</span>}
                </button>
              </li>
            ))}
          </ul>
          )
        }
      </AsyncView>
    </div>
  );
}
