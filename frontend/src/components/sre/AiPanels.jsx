import React from 'react';
import { AiBadge, RiskBadge } from './Badges';
import { confidenceBand, formatConfidence, formatDateTime } from '../../services/incidentService';
import { getActionLabel } from '../../lib/labels';
import { IconPredict, IconSparkles, IconBug } from '../Icons';

/**
 * AI investigation panel (frontend PRD section 7).
 *
 * The whole visual treatment exists to make one thing unmistakable: this is what a model
 * concluded, not what the system measured. AI-derived content sits inside a distinctly styled
 * container, carries an explicit badge, and states its confidence as an estimate.
 */
export function AiInvestigationPanel({ diagnosis, failureReason, onRetry, retrying, stale }) {
  if (!diagnosis) {
    return (
      <section className="panel ai-panel ai-panel-pending">
        <PanelHeader icon={<IconBug className="w-5 h-5" />} title="AI Investigation" />

        <p className="panel-pending-text">
          {failureReason
            ? failureReason
            : 'No AI diagnosis yet. The incident has been detected and evidence is being collected.'}
        </p>

        {/* Detection stands on its own, so an AI outage leaves a usable incident plus a retry. */}
        {onRetry && (
          <button type="button" className="secondary-btn" onClick={onRetry} disabled={retrying}>
            {retrying ? 'Requesting...' : 'Retry AI investigation'}
          </button>
        )}
      </section>
    );
  }

  const band = confidenceBand(diagnosis.confidence);

  return (
    <section className="panel ai-panel">
      <PanelHeader
        icon={<IconBug className="w-5 h-5" />}
        title="AI Investigation"
        badge={<AiBadge />}
      />

      {/* An open incident keeps absorbing evidence, so a diagnosis can end up older than the
          signals listed above it. Saying so is more useful than letting it look current. */}
      {stale && (
        <div className="ai-stale-notice">
          <span>New signals have correlated in since this diagnosis was produced.</span>
          {onRetry && (
            <button type="button" className="secondary-btn" onClick={onRetry} disabled={retrying}>
              {retrying ? 'Re-investigating...' : 'Re-investigate'}
            </button>
          )}
        </div>
      )}

      {diagnosis.summary && <p className="ai-summary">{diagnosis.summary}</p>}

      <div className="ai-field">
        <span className="ai-field-label">Likely root cause</span>
        <p className="ai-root-cause">{diagnosis.rootCause}</p>
      </div>

      {diagnosis.contributingFactors?.length > 0 && (
        <div className="ai-field">
          <span className="ai-field-label">Contributing factors</span>
          <ul className="ai-list">
            {diagnosis.contributingFactors.map((factor, index) => (
              <li key={index}>{factor}</li>
            ))}
          </ul>
        </div>
      )}

      {diagnosis.evidence?.length > 0 && (
        <div className="ai-field">
          <span className="ai-field-label">Evidence cited</span>
          <ul className="ai-list ai-evidence-list">
            {diagnosis.evidence.map((item, index) => (
              <li key={index}>{item}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="ai-footer">
        <div className={`confidence-meter confidence-${band}`}>
          <div className="confidence-bar">
            <div
              className="confidence-fill"
              style={{ width: `${Math.round((diagnosis.confidence || 0) * 100)}%` }}
            />
          </div>
          <span className="confidence-label">
            Confidence: {formatConfidence(diagnosis.confidence)}
          </span>
        </div>

        <div className="ai-provenance">
          {diagnosis.provider && <span>{diagnosis.provider}</span>}
          {diagnosis.model && <span>{diagnosis.model}</span>}
          {diagnosis.generatedAt && <span>{formatDateTime(diagnosis.generatedAt)}</span>}
        </div>
      </div>

      <p className="ai-disclaimer">
        This is a model-generated hypothesis based on the collected evidence, not a verified fact.
      </p>
    </section>
  );
}

/** Prediction panel (frontend PRD section 8). Always labelled as a prediction. */
export function PredictionPanel({ prediction }) {
  if (!prediction) return null;

  return (
    <section className="panel ai-panel prediction-panel">
      <PanelHeader
        icon={<IconPredict className="w-5 h-5" />}
        title="Predicted Impact"
        badge={<AiBadge label="AI prediction" />}
      />

      <p className="prediction-text">{prediction.predictedFailure}</p>

      <div className="prediction-risk">
        <span className="ai-field-label">Estimated risk if unaddressed</span>
        <RiskBadge risk={prediction.estimatedRisk} />
      </div>

      <p className="ai-disclaimer">
        A projection of what may happen if the incident continues, not an observed outcome.
      </p>
    </section>
  );
}

/**
 * Recommendation panel (frontend PRD section 9).
 *
 * Generating a recommendation does not run it, and the UI must not imply otherwise. Approval lives
 * in its own panel; this one only ever describes.
 */
export function RecommendationPanel({ recommendations }) {
  if (!recommendations?.length) return null;

  return (
    <section className="panel ai-panel recommendation-panel">
      <PanelHeader
        icon={<IconSparkles className="w-5 h-5" />}
        title="Recommended Actions"
        badge={<AiBadge label="AI recommendation" />}
      />

      <ul className="recommendation-list">
        {recommendations.map((rec, index) => (
          <li key={index} className={`recommendation-item ${rec.isRegisteredTool ? '' : 'not-executable'}`}>
            <div className="recommendation-head">
              <code className="recommendation-action" title={rec.action}>{getActionLabel(rec.action)}</code>
              <RiskBadge risk={rec.riskLevel} />
            </div>

            {rec.reason && (
              <p className="recommendation-line">
                <span className="ai-field-label">Why</span> {rec.reason}
              </p>
            )}

            {rec.expectedOutcome && (
              <p className="recommendation-line">
                <span className="ai-field-label">Expected outcome</span> {rec.expectedOutcome}
              </p>
            )}

            {/* A refused recommendation is still shown, with the reason, rather than hidden. */}
            {!rec.isRegisteredTool && (
              <p className="recommendation-blocked">
                Not executable: {rec.policyNote || 'this action is not a registered remediation tool.'}
              </p>
            )}
          </li>
        ))}
      </ul>

      <p className="ai-disclaimer">
        Recommendations are proposals only. Nothing runs until an operator approves it below.
      </p>
    </section>
  );
}

function PanelHeader({ icon, title, badge }) {
  return (
    <div className="panel-header">
      <span className="panel-icon">{icon}</span>
      <h4>{title}</h4>
      {badge}
    </div>
  );
}
