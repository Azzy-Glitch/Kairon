import React from 'react';
import { humanize } from './Badges';
import { formatDateTime } from '../../services/incidentService';
import { IconHistory } from '../Icons';

/**
 * Lifecycle rail plus the audit timeline (frontend PRD section 6).
 *
 * The rail is derived from backend state, and the timeline below it is the backend's own audit
 * trail - not a client-side reconstruction. Every entry carries a timestamp and an actor, so
 * "who approved this" is answerable from the screen.
 */
export function LifecycleRail({ stages }) {
  if (!stages?.length) return null;

  return (
    <ol className="lifecycle-rail">
      {stages.map((stage) => (
        <li key={stage.key} className={`lifecycle-stage stage-${stage.state}`}>
          <span className="lifecycle-dot" />
          <span className="lifecycle-label">{stage.label}</span>
        </li>
      ))}
    </ol>
  );
}

export function IncidentTimeline({ events }) {
  if (!events?.length) {
    return (
      <section className="panel timeline-panel">
        <PanelHeader />
        <p className="panel-pending-text">No timeline entries yet.</p>
      </section>
    );
  }

  return (
    <section className="panel timeline-panel">
      <PanelHeader />

      <ol className="timeline-list">
        {events.map((event) => (
          <li key={event.id} className={`timeline-entry timeline-${toneFor(event)}`}>
            <span className="timeline-dot" />

            <div className="timeline-body">
              <div className="timeline-head">
                <span className="timeline-type">{humanize(event.eventType)}</span>
                <span className="timeline-time">{formatDateTime(event.timestamp)}</span>
              </div>

              <div className="timeline-meta">
                <span className="timeline-actor">{event.actor}</span>
                {event.previousState && event.newState && (
                  <span className="timeline-transition">
                    {humanize(event.previousState)} to {humanize(event.newState)}
                  </span>
                )}
                {event.actionId && <span className="timeline-action">{event.actionId}</span>}
              </div>

              {event.message && <p className="timeline-message">{event.message}</p>}

              {/* Backend errors are already scrubbed of secrets and stack traces before they
                  reach the audit trail, so this is safe to render verbatim. */}
              {event.error && <p className="timeline-error">{event.error}</p>}
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}

function PanelHeader() {
  return (
    <div className="panel-header">
      <span className="panel-icon">
        <IconHistory className="w-5 h-5" />
      </span>
      <h4>Timeline</h4>
    </div>
  );
}

function toneFor(event) {
  if (event.eventType === 'Failed' || event.error) return 'bad';
  if (event.eventType === 'Resolved' || event.eventType === 'Verified') return 'good';
  if (event.eventType === 'Approved' || event.eventType === 'AwaitingApproval') return 'attention';
  return 'neutral';
}
