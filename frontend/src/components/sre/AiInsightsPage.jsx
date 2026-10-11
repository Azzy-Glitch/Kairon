import React, { useMemo } from 'react';
import { AiBadge, SeverityBadge } from './Badges';
import { AsyncView } from './StateViews';
import SourceFilterBar from '../ui/SourceFilterBar';
import { useFilteredBySource } from '../../lib/SourceFilterContext';
import { resolveSource } from '../../lib/source';
import { getActionLabel } from '../../lib/labels';
import HorizontalBarChart from '../ui/charts/HorizontalBarChart';
import { useEmergingRisks, useIncidents, useIncidentDetails } from '../../hooks/useIncidents';
import { useHealth } from '../../hooks/useHealth';
import { describeAiStatus } from '../../lib/aiStatus';
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
const RISK_KIND_LABELS = {
  capacity: 'Capacity',
  precursor: 'Early warning',
  backlog: 'Backlog',
  dependency: 'Dependency',
  recurrence: 'Recurring'
};

/**
 * Emerging risks: what the last hour of measurements and the last day of incidents suggest may go
 * wrong next. Trend-based and deterministic (not the AI); a time to threshold appears only when the
 * trend is consistent enough to extrapolate, and a metric already over its threshold is an incident,
 * not a prediction.
 */
export function EmergingRisks() {
  const query = useEmergingRisks();
  const data = query.data;
  if (!data) return null;
  const risks = data.risks || [];
  const insufficient = data.insufficientData || [];

  return (
    <section className="panel emerging-risks" aria-label="Emerging risks">
      <div className="panel-header">
        <h4>Emerging risks</h4>
        <span className="status-badge status-neutral">Trend analysis</span>
      </div>
      {risks.length === 0 ? (
        <p className="panel-pending-text">
          No emerging risks in the last {data.windowMinutes || 60} minutes: no metric is trending toward its threshold, and
          no dependency or incident keeps recurring.
        </p>
      ) : (
        <ul className="emerging-risk-list">
          {risks.map((risk, index) => (
            <li key={`${risk.kind}-${risk.service}-${risk.component}-${index}`} className="emerging-risk">
              <div className="emerging-risk-head">
                <span className="status-badge status-attention">{RISK_KIND_LABELS[risk.kind] || risk.kind}</span>
                <strong>{risk.title}</strong>
                <span className="emerging-risk-scope">{risk.service} · {risk.environment}</span>
                {risk.horizonMinutes != null && (
                  <span className="emerging-risk-horizon">threshold in ~{formatHorizon(risk.horizonMinutes)} if the trend continues</span>
                )}
              </div>
              <p className="recommendation-line">{risk.observation}</p>
              <p className="recommendation-line"><span className="ai-field-label">Possible consequence</span> {risk.consequence}</p>
              <p className="recommendation-line"><span className="ai-field-label">Preventive action</span> {risk.preventiveAction}</p>
              <span className="forecast-meta">Data confidence {Math.round((risk.confidence || 0) * 100)}% · {risk.samples} sample(s)</span>
            </li>
          ))}
        </ul>
      )}
      {insufficient.length > 0 && (
        <p className="forecast-meta">
          Not enough data yet for: {insufficient.map((i) => `${i.service} (${i.environment})`).join(', ')}.
        </p>
      )}
      <p className="ai-disclaimer">
        Projections from observed trends, not certainties - a trend can level off or reverse.
      </p>
    </section>
  );
}

function formatHorizon(minutes) {
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours} h ${rest} min` : `${hours} h`;
}

export default function AiInsightsPage() {
  const feed = useIncidents({ status: '', pollMs: 10000 });
  const details = useIncidentDetails(feed.incidents, { limit: 20 });
  const { health } = useHealth();
  const aiStatus = describeAiStatus(health);

  const filteredDetails = useFilteredBySource(details.data || [], resolveSource);

  const diagnosed = useMemo(
    () => filteredDetails.filter((i) => i.diagnosis).sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt)),
    [filteredDetails]
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
    return [...map.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5);
  }, [diagnosed]);

  const recommendationGroups = useMemo(() => {
    const map = new Map();
    // diagnosed is sorted most-recent-first (see the sort above), so the first time an action
    // type is encountered while iterating is already its most recent occurrence.
    for (const incident of diagnosed) {
      for (const rec of incident.recommendations || []) {
        if (!rec.action) continue;
        const existing = map.get(rec.action);
        if (existing) {
          existing.count += 1;
        } else {
          map.set(rec.action, { action: rec.action, count: 1, lastSeenAt: incident.updatedAt });
        }
      }
    }
    return [...map.values()].sort((a, b) => b.count - a.count);
  }, [diagnosed]);

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconSparkles className="w-6 h-6 tone-medium" />
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
          <span className={`status-dot ${aiStatus.tone === 'good' ? 'online' : 'offline'}`} />
          <span>{aiStatus.label}</span>
        </div>
      </div>

      <EmergingRisks />

      <SourceFilterBar records={details.data || []} className="source-filter-bar" />

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
                <HorizontalBarChart
                  data={rootCauseCounts.map(([cause, count]) => ({ label: cause, value: count }))}
                  color="var(--series-2)"
                />
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

            {recommendationGroups.length > 0 && (
              <div className="ai-insights-block">
                <h4 className="subheading">Recommendation history</h4>
                <ul className="recommendation-list">
                  {recommendationGroups.map((group) => (
                    <li key={group.action} className="recommendation-item">
                      <div className="recommendation-head">
                        <code className="recommendation-action" title={group.action}>{getActionLabel(group.action)}</code>
                        <span className="ai-insights-count">{group.count} {group.count === 1 ? 'time' : 'times'}</span>
                      </div>
                      {group.lastSeenAt && (
                        <p className="recommendation-line">Most recent {formatDateTime(group.lastSeenAt)}</p>
                      )}
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
  const distribution = [
    { label: 'High', value: counts.high },
    { label: 'Moderate', value: counts.moderate },
    { label: 'Low', value: counts.low }
  ];

  return (
    <div className="ai-insights-tile ai-insights-tile-wide">
      <span className="ai-insights-tile-label">Confidence distribution <AiBadge label="AI estimate" /></span>
      {total === 0 ? (
        <span className="panel-pending-text">No diagnoses yet</span>
      ) : (
        <HorizontalBarChart data={distribution} color="var(--series-1)" />
      )}
    </div>
  );
}
