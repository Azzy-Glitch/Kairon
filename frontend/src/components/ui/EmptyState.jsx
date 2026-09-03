import React from 'react';

/**
 * Shared EmptyState (redesign brief section 4). Icon, one-line explanation of why it's empty,
 * and a primary button that fixes it - never just "No records found" with no way to act.
 */
export default function EmptyState({ icon, title, description, action, className = '' }) {
  return (
    <div className={`ui-empty-state ${className}`}>
      {icon && <div className="ui-empty-state-icon">{icon}</div>}
      {title && <div className="ui-empty-state-title">{title}</div>}
      {description && <p className="ui-empty-state-description">{description}</p>}
      {action && <div className="ui-empty-state-action">{action}</div>}
    </div>
  );
}
