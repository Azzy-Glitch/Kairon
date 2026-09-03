import React from 'react';

/**
 * Shared Tabs / FilterChips (redesign brief section 4). Pill chips, --surface-sunken inactive,
 * --accent-soft + --accent text active, count in parentheses. Replaces the native-button tabs on
 * Diagnostics and the ad-hoc filters on Actions/Audit trail.
 *
 * items: [{ id, label, count? }]
 */
export default function Tabs({ items, activeId, onChange, className = '' }) {
  return (
    <div className={`ui-tabs ${className}`} role="tablist">
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          role="tab"
          aria-selected={activeId === item.id}
          className={`ui-tabs-chip ${activeId === item.id ? 'active' : ''}`}
          onClick={() => onChange(item.id)}
        >
          {item.label}
          {item.count !== undefined && <span className="ui-tabs-chip-count">({item.count})</span>}
        </button>
      ))}
    </div>
  );
}
