import React from 'react';
import Tabs from './Tabs';
import { useSourceFilter } from '../../lib/SourceFilterContext';
import { countBySource, sourceMeta } from '../../lib/source';

/**
 * The source filter chip row (brief section 6): "All sources (18) - .NET app (16) - Python app
 * (2)". Drops directly under the page header on Overview, Services, Live telemetry, Incidents and
 * Insights. Selection is shared (SourceFilterContext) so it stays in sync across all of them.
 *
 * Only renders sources that actually have at least one record connected, per the brief's own "if a
 * source is not currently connected, show a note instead of an empty section" rule extended here:
 * no point offering a filter chip for a source with zero records.
 */
export default function SourceFilterBar({ records, installationsByApplicationName, className = '' }) {
  const { selectedSource, setSelectedSource } = useSourceFilter();
  const counts = countBySource(records, installationsByApplicationName);

  const items = [{ id: 'all', label: 'All sources', count: counts.all }];
  for (const key of ['dotnet', 'python', 'agent']) {
    if (counts[key] > 0) items.push({ id: key, label: sourceMeta[key].label, count: counts[key] });
  }

  if (items.length <= 1) return null; // Only one (or zero) source connected - nothing to filter.

  return <Tabs items={items} activeId={selectedSource} onChange={setSelectedSource} className={className} />;
}
