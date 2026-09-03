import React from 'react';

const TONES = ['critical', 'high', 'medium', 'low', 'healthy', 'neutral'];

/**
 * Shared Badge (redesign brief section 4). Soft background + matching text + 1px border in the
 * same hue at 30% alpha. Sentence case content is the caller's responsibility (labels.js maps
 * system values to sentence-case strings before they ever reach this component).
 *
 * size: 'sm' | 'md'
 * tone: 'critical' | 'high' | 'medium' | 'low' | 'healthy' | 'neutral'
 */
export default function Badge({ size = 'sm', tone = 'neutral', children, className = '' }) {
  const safeTone = TONES.includes(tone) ? tone : 'neutral';
  const classes = ['ui-badge', `ui-badge-${size}`, `ui-badge-${safeTone}`, className].filter(Boolean).join(' ');
  return <span className={classes}>{children}</span>;
}
