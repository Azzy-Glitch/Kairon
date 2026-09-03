import React from 'react';
import DataTable from '../ui/DataTable';
import EmptyState from '../ui/EmptyState';
import { AsyncView } from './StateViews';
import { SeverityBadge, StatusBadge } from './Badges';
import { relativeTime } from '../../services/incidentService';
import { formatAbsoluteTime, SEVERITY_ORDER } from '../../lib/labels';
import { IconShield } from '../Icons';

function severityRank(severity) {
  const index = SEVERITY_ORDER.indexOf(severity);
  return index === -1 ? SEVERITY_ORDER.length : index;
}

const COLUMNS = [
  {
    key: 'severity',
    label: 'Severity',
    sortable: true,
    sortValue: (incident) => severityRank(incident.severity),
    render: (incident) => <SeverityBadge severity={incident.severity} size="sm" />
  },
  {
    key: 'title',
    label: 'Title',
    render: (incident) => (
      <>
        <div className="incidents-table-title">{incident.title}</div>
        <div className="incidents-table-subtle">{incident.incidentKey}</div>
      </>
    )
  },
  {
    key: 'service',
    label: 'Service',
    priority: 1,
    render: (incident) => incident.service || '—'
  },
  {
    key: 'status',
    label: 'Status',
    render: (incident) => <StatusBadge status={incident.status} />
  },
  {
    key: 'detectedAt',
    label: 'Detected',
    align: 'right',
    mono: true,
    sortable: true,
    priority: 1,
    sortValue: (incident) => (incident.detectedAt ? new Date(incident.detectedAt).getTime() : 0),
    render: (incident) => (
      <span title={formatAbsoluteTime(incident.detectedAt)}>{relativeTime(incident.detectedAt)}</span>
    )
  }
];

/**
 * Table view of the incident feed (redesign brief section 8: Incidents), built on the shared
 * DataTable. Same data, same filters and the same row-click-opens-detail behaviour as the card
 * view (IncidentFeed) - this is just a denser, sortable layout for scanning many incidents at
 * once. `incidents` arrives already filtered (status/severity/service/time range - see
 * IncidentsPage.jsx); `query` is only consulted here for the loading/error/no-data-at-all states.
 */
export default function IncidentsTable({ query, incidents, onSelect }) {
  return (
    <div className="incidents-table-panel">
      <AsyncView
        query={query}
        loadingLabel="Loading incidents..."
        emptyTitle="No incidents"
        emptyHint="Nothing is currently degraded. Start the demo scenario to see the pipeline run."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() => (
          <DataTable
            columns={COLUMNS}
            rows={incidents}
            getRowKey={(incident) => incident.id}
            onRowClick={(incident) => onSelect(incident.id)}
            emptyState={
              <EmptyState
                icon={<IconShield className="w-10 h-10" />}
                title="No matching incidents"
                description="Try widening the filters above."
              />
            }
          />
        )}
      </AsyncView>
    </div>
  );
}
