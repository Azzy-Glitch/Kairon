import React from 'react';

/**
 * Shared Card (redesign brief section 4). --surface, 1px border, 10px radius, 20px padding, no
 * shadow (borders over shadows - shadow is reserved for overlays only).
 *
 * accentSide: 'critical' | 'high' | 'medium' | 'low' | 'healthy' | undefined - a 3px status left
 * border. The card body itself always stays --surface; status color is never a full background
 * wash (brief section 2, color rules).
 */
export default function Card({ accentSide, title, badge, action, children, className = '', ...rest }) {
  const classes = ['ui-card', accentSide ? `ui-card-accent-${accentSide}` : '', className].filter(Boolean).join(' ');
  const hasHeader = title || badge || action;

  return (
    <div className={classes} {...rest}>
      {hasHeader && (
        <div className="ui-card-header">
          <div className="ui-card-header-title">
            {title && <h3>{title}</h3>}
            {badge}
          </div>
          {action && <div className="ui-card-header-action">{action}</div>}
        </div>
      )}
      <div className="ui-card-body">{children}</div>
    </div>
  );
}
