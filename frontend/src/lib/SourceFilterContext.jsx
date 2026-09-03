import React, { createContext, useContext, useEffect, useState } from 'react';
import { SDK_SOURCES } from './source';

const QUERY_KEY = 'source';
const SourceFilterCtx = createContext(null);

function readInitialSource() {
  try {
    const fromQuery = new URLSearchParams(window.location.search).get(QUERY_KEY);
    if (fromQuery && (fromQuery === 'all' || SDK_SOURCES.includes(fromQuery))) return fromQuery;
  } catch {
    // window/location can be unavailable in non-browser test environments - fall through.
  }
  return 'all';
}

/**
 * Persists the selected source filter across every page that uses it (Overview, Services, Live
 * telemetry, Incidents, Insights - brief section 6), and reflects it in the URL query string so a
 * demo can deep-link straight to e.g. ?source=python. There is no router in this app (navigation
 * is plain component state in App.jsx), so this uses the History API directly rather than adding
 * one just for this.
 */
export function SourceFilterProvider({ children }) {
  const [selectedSource, setSelectedSourceState] = useState(readInitialSource);

  const setSelectedSource = (value) => {
    setSelectedSourceState(value);
    try {
      const url = new URL(window.location.href);
      if (value === 'all') url.searchParams.delete(QUERY_KEY);
      else url.searchParams.set(QUERY_KEY, value);
      window.history.replaceState({}, '', url);
    } catch {
      // Non-fatal: the in-memory selection still works even if the URL can't be updated.
    }
  };

  return (
    <SourceFilterCtx.Provider value={{ selectedSource, setSelectedSource }}>
      {children}
    </SourceFilterCtx.Provider>
  );
}

export function useSourceFilter() {
  const ctx = useContext(SourceFilterCtx);
  if (!ctx) throw new Error('useSourceFilter must be used within a SourceFilterProvider');
  return ctx;
}

/** Filters a list of records down to the currently-selected source, using resolveSource. 'all'
 * (the default) returns every record unfiltered. */
export function useFilteredBySource(records, resolveFn) {
  const { selectedSource } = useSourceFilter();
  if (selectedSource === 'all') return records;
  return records.filter((r) => resolveFn(r) === selectedSource);
}
