import React, { useEffect, useMemo, useState } from 'react';
import { IconChevronDown } from '../Icons';

function useIsCompact(breakpointPx = 1100) {
  const [isCompact, setIsCompact] = useState(
    () => typeof window !== 'undefined' && window.matchMedia(`(max-width: ${breakpointPx}px)`).matches
  );

  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${breakpointPx}px)`);
    const onChange = (e) => setIsCompact(e.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, [breakpointPx]);

  return isCompact;
}

/**
 * Shared DataTable (redesign brief section 4). Sticky header, 40px rows, no zebra striping, 1px
 * row dividers, accent-soft hover, right-aligned numeric columns in mono.
 *
 * Responsive without an inner horizontal scrollbar: below 1100px, columns with priority >= 2
 * collapse out of the row and into an expandable detail row instead, rather than the whole table
 * scrolling sideways.
 *
 * columns: [{ key, label, align: 'left'|'right', mono: bool, priority: 0|1|2, sortable: bool,
 *             render?: (row) => node }]
 */
export default function DataTable({ columns, rows, getRowKey, onRowClick, emptyState, sortableDefaultKey }) {
  const isCompact = useIsCompact();
  const [expanded, setExpanded] = useState(() => new Set());
  const [sort, setSort] = useState(sortableDefaultKey ? { key: sortableDefaultKey, dir: 'desc' } : null);

  const visibleColumns = isCompact ? columns.filter((c) => (c.priority || 0) < 2) : columns;
  const collapsedColumns = isCompact ? columns.filter((c) => (c.priority || 0) >= 2) : [];

  const sortedRows = useMemo(() => {
    if (!sort) return rows;
    const col = columns.find((c) => c.key === sort.key);
    if (!col) return rows;
    const sorted = [...rows].sort((a, b) => {
      const av = col.sortValue ? col.sortValue(a) : a[sort.key];
      const bv = col.sortValue ? col.sortValue(b) : b[sort.key];
      if (av === bv) return 0;
      return av > bv ? 1 : -1;
    });
    return sort.dir === 'desc' ? sorted.reverse() : sorted;
  }, [rows, sort, columns]);

  const toggleSort = (key) => {
    setSort((prev) => {
      if (!prev || prev.key !== key) return { key, dir: 'desc' };
      return { key, dir: prev.dir === 'desc' ? 'asc' : 'desc' };
    });
  };

  const toggleExpanded = (rowKey) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(rowKey)) next.delete(rowKey);
      else next.add(rowKey);
      return next;
    });
  };

  if (!rows || rows.length === 0) {
    return <div className="ui-datatable-empty-wrap">{emptyState}</div>;
  }

  return (
    <div className="ui-datatable-wrap">
      <table className="ui-datatable">
        <thead>
          <tr>
            {collapsedColumns.length > 0 && <th className="ui-datatable-expand-col" aria-hidden="true" />}
            {visibleColumns.map((col) => (
              <th
                key={col.key}
                className={col.align === 'right' ? 'ui-datatable-align-right' : ''}
                aria-sort={sort?.key === col.key ? (sort.dir === 'asc' ? 'ascending' : 'descending') : undefined}
              >
                {col.sortable ? (
                  <button type="button" className="ui-datatable-sort-btn" onClick={() => toggleSort(col.key)}>
                    {col.label}
                    {sort?.key === col.key && <span className="ui-datatable-sort-arrow">{sort.dir === 'asc' ? '↑' : '↓'}</span>}
                  </button>
                ) : (
                  col.label
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sortedRows.map((row) => {
            const key = getRowKey(row);
            const isExpanded = expanded.has(key);
            return (
              <React.Fragment key={key}>
                <tr className={onRowClick ? 'ui-datatable-row-clickable' : ''} onClick={onRowClick ? () => onRowClick(row) : undefined}>
                  {collapsedColumns.length > 0 && (
                    <td className="ui-datatable-expand-col">
                      <button
                        type="button"
                        className={`ui-datatable-expand-btn ${isExpanded ? 'open' : ''}`}
                        onClick={(e) => {
                          e.stopPropagation();
                          toggleExpanded(key);
                        }}
                        aria-expanded={isExpanded}
                        aria-label={isExpanded ? 'Hide details' : 'Show details'}
                      >
                        <IconChevronDown className="w-3.5 h-3.5" />
                      </button>
                    </td>
                  )}
                  {visibleColumns.map((col) => (
                    <td key={col.key} className={[col.align === 'right' ? 'ui-datatable-align-right' : '', col.mono ? 'ui-datatable-mono' : ''].filter(Boolean).join(' ')}>
                      {col.render ? col.render(row) : row[col.key]}
                    </td>
                  ))}
                </tr>
                {isExpanded && collapsedColumns.length > 0 && (
                  <tr className="ui-datatable-detail-row">
                    <td colSpan={visibleColumns.length + 1}>
                      <dl className="ui-datatable-detail-list">
                        {collapsedColumns.map((col) => (
                          <div key={col.key} className="ui-datatable-detail-item">
                            <dt>{col.label}</dt>
                            <dd className={col.mono ? 'ui-datatable-mono' : ''}>{col.render ? col.render(row) : row[col.key]}</dd>
                          </div>
                        ))}
                      </dl>
                    </td>
                  </tr>
                )}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
