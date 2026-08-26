import React, { useMemo } from 'react';
import { AiBadge, SeverityBadge } from './Badges';
import { AsyncView } from './StateViews';
import { useIncidents, useIncidentDetails } from '../../hooks/useIncidents';
import { useHealth } from '../../hooks/useDemo';
import { confidenceBand, formatConfidence, formatDateTime } from '../../services/incidentService';
import { IconSparkles, IconBug } from '../Icons';

/**
 * Aggregated AI intelligence view (frontend PRD section 26).
 *
 * Composed from existing incident detail responses - the backend has no separate "AI insights"
 * endpoint, and the PRD explicitly permits composing this client-side rather than requiring one.
 * Bounded to the most recent incidents via useIncidentDetails so this page's request volume does
 * not grow unbounded with incident history.
 */
export default function AiInsightsPage() {
  const feed = useIncidents({ status: '', pollMs: 10000 });
  const details = useIncidentDetails(feed.incidents, { limit: 20 });
  const { health } = useHealth();

  const diagnosed = useMemo(
    () => (details.data || []).filter((i) => i.diagnosis).sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt)),
    [details.data]
  );

  const confidenceCounts = useMemo(() => {
    const counts = { high: 0, moderate: 0, low: 0, unknown: 0 };
    for (const incident of diagnosed) {
      counts[confidenceBand(incident.diagnosis?.confidence)] += 1;
    }
    return counts;
  }, [diagnosed]);

  const rootCauseCounts = useMemo(() => {
    const map = new Map();
    for (const incident of diagnosed) {
      const cause = incident.diagnosis?.rootCause;
      if (!cause) continue;
      map.set(cause, (map.get(cause) || 0) + 1);
    }
    return [...map.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8);
  }, [diagnosed]);

  const recommendations = useMemo(
    () =>
      diagnosed
        .flatMap((incident) =>
          (incident.recommendations || []).map((rec) => ({ ...rec, incidentKey: incident.incidentKey }))
        )
        .slice(0, 15),
    [diagnosed]
  );

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconSparkles className="w-6 h-6 text-amber-500" />
          </div>
          <div>
            <h3>AI Insights</h3>
            <p className="section-desc">
              What Kairon's AI layer has concluded across recent incidents - diagnoses, confidence and
              recommendations, all traceable back to the incident that produced them
            </p>
          </div>
        </div>

        <div className="ai-insights-provider">
          <span className="ai-insights-provider-label">AI service</span>
          <span className={`status-dot ${health.aiService ? 'online' : 'offline'}`} />
          <span>{health.aiMode && health.aiMode !== 'unknown' ? health.aiMode : health.aiService ? 'operational' : 'unavailable'}</span>
        </div>
      </div>

      <AsyncView
        query={feed}
        loadingLabel="Loading incidents..."
        emptyTitle="No AI activity yet"
        emptyHint="Kairon investigates automatically once an incident is detected. Run the incident simulation to see this populate."
        emptyIcon={<IconBug className="w-10 h-10" />}
      >
        {() => (
          <>
            <div className="ai-insights-summary-grid">
              <div className="ai-insights-tile">
                <span className="ai-insights-tile-label">Diagnosed incidents</span>
                <span className="ai-insights-tile-value">{diagnosed.length}</span>
              </div>
              <ConfidenceBar counts={confidenceCounts} />
            </div>

            {rootCauseCounts.length > 0 && (
              <div className="ai-insights-block">
                <h4 className="subheading">Recurring root causes</h4>
                <ul className="ai-list">
                  {rootCauseCounts.map(([cause, count]) => (
                    <li key={cause}>
                      {cause} <span className="ai-insights-count">&times;{count}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <div className="ai-insights-block">
              <h4 className="subheading">Recent diagnoses</h4>
              {diagnosed.length === 0 ? (
                <p className="panel-pending-text">No diagnoses yet.</p>
              ) : (
                <div className="table-responsive">
                  <table className="custom-table">
                    <thead>
                      <tr>
                        <th>Incident</th>
                        <th>Severity</th>
                        <th>Root cause</th>
                        <th>Confidence</th>
                        <th>Diagnosed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {diagnosed.slice(0, 15).map((incident) => (
                        <tr key={incident.id}>
                          <td><code className="path-code">{incident.incidentKey}</code></td>
                          <td><SeverityBadge severity={incident.severity} size="sm" /></td>
                          <td>{incident.diagnosis.rootCause}</td>
                          <td>{formatConfidence(incident.diagnosis.confidence)}</td>
                          <td>{formatDateTime(incident.diagnosis.generatedAt)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>

            {recommendations.length > 0 && (
              <div className="ai-insights-block">
                <h4 className="subheading">Recommendation history</h4>
                <ul className="recommendation-list">
                  {recommendations.map((rec, index) => (
                    <li key={index} className="recommendation-item">
                      <div className="recommendation-head">
                        <code className="recommendation-action">{rec.action}</code>
                        <span className="ai-insights-count">{rec.incidentKey}</span>
                      </div>
                      {rec.reason && <p className="recommendation-line">{rec.reason}</p>}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
        )}
      </AsyncView>
    </div>
  );
}

function ConfidenceBar({ counts }) {
  const total = counts.high + counts.moderate + counts.low + counts.unknown;

  return (
    <div className="ai-insights-tile ai-insights-tile-wide">
      <span className="ai-insights-tile-label">Confidence distribution <AiBadge label="AI estimate" /></span>
      {total === 0 ? (
        <span className="panel-pending-text">No diagnoses yet</span>
      ) : (
        <div className="confidence-distribution">
          <ConfidenceSegment label="High" count={counts.high} total={total} tone="high" />
          <ConfidenceSegment label="Moderate" count={counts.moderate} total={total} tone="moderate" />
          <ConfidenceSegment label="Low" count={counts.low} total={total} tone="low" />
        </div>
      )}
    </div>
  );
}

function ConfidenceSegment({ label, count, total, tone }) {
  const pct = total > 0 ? Math.round((count / total) * 100) : 0;
  return (
    <div className="confidence-segment">
      <span className="confidence-segment-label">{label}</span>
      <div className="confidence-segment-track">
        <div className={`confidence-segment-fill confidence-${tone}`} style={{ width: `${pct}%` }} />
      </div>
      <span className="confidence-segment-count">{count}</span>
    </div>
  );
}
