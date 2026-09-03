import React from 'react';

/**
 * Shared StatusTimeline (redesign brief section 4) - the Detected -> ... -> Resolved rail.
 * Completed steps: filled --healthy with a connecting rule. Current step: ringed --accent with a
 * subtle pulse (disabled under prefers-reduced-motion via CSS). Future steps: --border-strong
 * hollow. Horizontal by default, vertical on narrow widths (handled in CSS, not here).
 *
 * steps: [{ key, label }] - label must already be sentence case (labels.js's job, not this
 * component's).
 * currentKey: the step key currently active. Everything before it in `steps` is "completed",
 * everything after is "future".
 */
export default function StatusTimeline({ steps, currentKey, className = '' }) {
  const currentIndex = steps.findIndex((s) => s.key === currentKey);

  return (
    <ol className={`ui-status-timeline ${className}`}>
      {steps.map((step, i) => {
        const state = currentIndex === -1 ? 'future' : i < currentIndex ? 'completed' : i === currentIndex ? 'current' : 'future';
        return (
          <li key={step.key} className={`ui-status-timeline-step ui-status-timeline-step-${state}`}>
            <span className="ui-status-timeline-node" aria-hidden="true" />
            <span className="ui-status-timeline-label">{step.label}</span>
          </li>
        );
      })}
    </ol>
  );
}
