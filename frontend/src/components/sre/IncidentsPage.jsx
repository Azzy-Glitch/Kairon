import React, { useEffect, useMemo, useState } from 'react';
import IncidentFeed from './IncidentFeed';
import IncidentsTable from './IncidentsTable';
import IncidentDetail from './IncidentDetail';
import SourceFilterBar from '../ui/SourceFilterBar';
import Tabs from '../ui/Tabs';
import { useFilteredBySource } from '../../lib/SourceFilterContext';
import { resolveSource } from '../../lib/source';
import { SEVERITY_ORDER } from '../../lib/labels';
import { useIncident, useIncidentActions, useIncidents } from '../../hooks/useIncidents';

const VIEW_OPTIONS = [
  { id: 'table', label: 'Table' },
  { id: 'cards', label: 'Cards' }
];

const STATUS_FILTERS = [
  { id: 'active', label: 'Active' },
  { id: 'all', label: 'All' },
  { id: 'Resolved', label: 'Resolved' }
];

const SEVERITY_FILTERS = [{ id: 'all', label: 'All severities' }, ...SEVERITY_ORDER.map((s) => ({ id: s, label: s }))];

const TIME_RANGE_FILTERS = [
  { id: 'all', label: 'All time' },
  { id: '1h', label: 'Last hour' },
  { id: '24h', label: 'Last 24h' },
  { id: '7d', label: 'Last 7d' }
];

const TIME_RANGE_MS = { '1h': 60 * 60 * 1000, '24h': 24 * 60 * 60 * 1000, '7d': 7 * 24 * 60 * 60 * 1000 };

/** True unless a specific window is selected and the incident falls outside it (or has no usable
 * detectedAt to check against - a timestamp-less incident can't be confirmed "recent"). */
function withinTimeRange(incident, timeRange) {
  const windowMs = TIME_RANGE_MS[timeRange];
  if (!windowMs) return true;
  const detected = incident.detectedAt ? new Date(incident.detectedAt).getTime() : NaN;
  if (Number.isNaN(detected)) return false;
  return Date.now() - detected <= windowMs;
}

/**
 * Incidents (Kairon frontend redesign brief, section 8): the full, standalone incident list and
 * detail workspace - split out of the old combined Overview+Incidents screen so a real incident
 * never appears twice on two different tabs at once. Overview keeps only a short "needs attention"
 * glance list that links back here; this page owns the entire filterable feed.
 *
 * Two view modes share one filter bar: a dense sortable DataTable (default) and the original
 * card/list feed. Only `status` is a backend query param (matches the feed's existing polling
 * hook); severity, service and time range are filtered client-side over whatever page of incidents
 * is already on screen - there is no backend endpoint for a service registry or a time-window
 * query, and the brief is explicit about not fabricating one for the hackathon.
 */
export default function IncidentsPage() {
  const [view, setView] = useState('table');
  const [filters, setFilters] = useState({ status: 'active', severity: 'all', service: 'all', timeRange: 'all' });
  const [selectedId, setSelectedId] = useState(null);

  const feed = useIncidents({ status: filters.status });
  const detail = useIncident(selectedId);

  const actions = useIncidentActions(async () => {
    await Promise.all([detail.reload({ silent: true }), feed.reload({ silent: true })]);
  });

  const sourceFiltered = useFilteredBySource(feed.incidents, resolveSource);

  // The service filter's own option list is derived from the source-filtered set (not the
  // severity/service/time-filtered set below), so picking a service doesn't make the other
  // services disappear from the dropdown.
  const serviceOptions = useMemo(
    () => Array.from(new Set(sourceFiltered.map((i) => i.service).filter(Boolean))).sort((a, b) => a.localeCompare(b)),
    [sourceFiltered]
  );

  const filteredIncidents = useMemo(
    () =>
      sourceFiltered.filter((incident) => {
        if (filters.severity !== 'all' && incident.severity !== filters.severity) return false;
        if (filters.service !== 'all' && incident.service !== filters.service) return false;
        if (!withinTimeRange(incident, filters.timeRange)) return false;
        return true;
      }),
    [sourceFiltered, filters.severity, filters.service, filters.timeRange]
  );

  // The most important open incident is selected by default, so the page is useful the moment it
  // loads rather than after a click. Only applies while nothing is selected, so a poll never yanks
  // the operator away from the incident they are reading.
  useEffect(() => {
    if (selectedId) return;
    const candidate = filteredIncidents[0]?.id;
    if (candidate) setSelectedId(candidate);
  }, [selectedId, filteredIncidents]);

  return (
    <div className="animate-fade-in">
      <SourceFilterBar records={feed.incidents} className="source-filter-bar" />

      <div className="incidents-toolbar">
        <div className="incidents-toolbar-row">
          <Tabs
            items={STATUS_FILTERS}
            activeId={filters.status === '' ? 'all' : filters.status}
            onChange={(id) => setFilters((f) => ({ ...f, status: id === 'all' ? '' : id }))}
          />
          <Tabs items={VIEW_OPTIONS} activeId={view} onChange={setView} className="incidents-view-toggle" />
        </div>

        <div className="incidents-toolbar-row">
          <Tabs
            items={SEVERITY_FILTERS}
            activeId={filters.severity}
            onChange={(id) => setFilters((f) => ({ ...f, severity: id }))}
          />

          <select
            className="approval-input incidents-service-select"
            value={filters.service}
            onChange={(e) => setFilters((f) => ({ ...f, service: e.target.value }))}
            aria-label="Filter by service"
          >
            <option value="all">All services</option>
            {serviceOptions.map((service) => (
              <option key={service} value={service}>
                {service}
              </option>
            ))}
          </select>

          <Tabs
            items={TIME_RANGE_FILTERS}
            activeId={filters.timeRange}
            onChange={(id) => setFilters((f) => ({ ...f, timeRange: id }))}
          />
        </div>
      </div>

      {view === 'table' ? (
        <div className="incidents-table-layout">
          <IncidentsTable query={feed} incidents={filteredIncidents} onSelect={setSelectedId} />

          <div className="sre-detail-pane">
            <IncidentDetail query={detail} actions={actions} />
          </div>
        </div>
      ) : (
        <div className="sre-workspace">
          <IncidentFeed query={feed} incidents={filteredIncidents} selectedId={selectedId} onSelect={setSelectedId} />

          <div className="sre-detail-pane">
            <IncidentDetail query={detail} actions={actions} />
          </div>
        </div>
      )}
    </div>
  );
}
