import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';

import IncidentDetail from './IncidentDetail';
import { AiInvestigationPanel, PredictionPanel, RecommendationPanel } from './AiPanels';
import { IncidentStatus } from '../../types/incident';

const elevated = {
  outcome: 'elevated',
  riskLevel: 'high',
  failureMode: 'Requests keep failing with server errors.',
  evidence: ['Error rate rising: 0% earlier, 60% recently (threshold 10%; over it in 100% of recent samples).'],
  horizonMinutes: 5,
  confidence: 0.6,
  expectedImpact: 'Users keep seeing failed requests until the cause is removed.',
  preventiveAction: 'Mitigate now, then fix the failing code path or dependency.',
  method: 'trend-baseline',
  trends: [{ metric: 'errorRate', label: 'Error rate', unit: '%', earlier: 0, recent: 60, threshold: 10, direction: 'rising', persistence: 1 }]
};

function renderIncident(overrides) {
  const incident = {
    id: 'i1', incidentKey: 'INC-0007', title: 'Checkout degraded', service: 'Checkout', environment: 'Development',
    status: IncidentStatus.RecommendationReady, severity: 'High', detectedAt: '2026-10-10T12:00:00Z',
    diagnosis: { summary: 's', rootCause: 'The app started returning server errors.', confidence: 0.6 },
    actions: [], recommendations: [], timeline: [],
    ...overrides
  };
  render(
    <IncidentDetail
      query={{ state: 'success', data: incident, isLoading: false, isError: false, isEmpty: false, isSuccess: true, reload: vi.fn() }}
      actions={{ approve: vi.fn(), reject: vi.fn(), investigate: vi.fn(), cancel: vi.fn(), busyActionId: null, actionError: null }}
    />
  );
}

describe('Future risk', () => {
  it("shows KAIRON's trend forecast with its evidence, horizon and confidence - even without an AI prediction", () => {
    render(<PredictionPanel forecast={elevated} />);

    expect(screen.getByText('Trend forecast')).toBeInTheDocument();
    expect(screen.getByText('Requests keep failing with server errors.')).toBeInTheDocument();
    expect(screen.getByText(/next 5 minutes/)).toBeInTheDocument();
    expect(screen.getByText(/data confidence 60%/)).toBeInTheDocument();
    expect(screen.getByText('Error rate')).toBeInTheDocument();
    expect(screen.getByText(/Mitigate now/)).toBeInTheDocument();
    expect(screen.queryByText('AI prediction')).not.toBeInTheDocument();
    expect(screen.getByText(/not a predicted failure time/)).toBeInTheDocument();
  });

  it('says plainly when there is not enough data to predict anything', () => {
    render(<PredictionPanel forecast={{ ...elevated, outcome: 'inconclusive', riskLevel: 'unknown', trends: [],
      evidence: ['Fewer than 4 metric samples in the evidence window - not enough to project a trend.'] }} />);

    expect(screen.getByText('Inconclusive')).toBeInTheDocument();
    expect(screen.getByText(/Not enough data to project a trend yet/)).toBeInTheDocument();
    expect(screen.queryByText(/next 5 minutes/)).not.toBeInTheDocument();
  });

  it("keeps the AI's own prediction separate from the measured trend", () => {
    render(<PredictionPanel forecast={elevated} prediction={{ predictedFailure: 'Checkout keeps failing', estimatedRisk: 'high' }} />);

    expect(screen.getByText('AI prediction')).toBeInTheDocument();
    expect(screen.getByText('What the AI expects')).toBeInTheDocument();
    expect(screen.getByText('Checkout keeps failing')).toBeInTheDocument();
  });
});

describe('Investigation certainty and next steps', () => {
  it('labels the root cause by how well the evidence supports it and lists what to check next', () => {
    render(
      <AiInvestigationPanel
        diagnosis={{ rootCause: 'Server errors', confidence: 0.55 }}
        assessment={{ rootCauseCertainty: 'possible', nextSteps: ['Review the code path that raises KeyError.'], consideredActions: [], offeredActions: [] }}
      />
    );

    expect(screen.getByText('Possible root cause')).toBeInTheDocument();
    expect(screen.getByText('What to check next')).toBeInTheDocument();
    expect(screen.getByText('Review the code path that raises KeyError.')).toBeInTheDocument();
  });

  it('keeps the original wording for incidents investigated before assessments existed', () => {
    render(<AiInvestigationPanel diagnosis={{ rootCause: 'Server errors', confidence: 0.55 }} />);
    expect(screen.getByText('Likely root cause')).toBeInTheDocument();
  });
});

describe('Recommendations and the alternatives the AI rejected', () => {
  it('shows actions the AI weighed and did not recommend, with its reason', () => {
    render(
      <RecommendationPanel
        recommendations={[{ action: 'RunHealthCheck', riskLevel: 'Low', isRegisteredTool: true, reason: 'Read-only check' }]}
        considered={[
          { action: 'RunHealthCheck', verdict: 'recommended', reason: 'Read-only check' },
          { action: 'RestartService', verdict: 'not_recommended', reason: 'The database is down; a restart will not bring it back.' }
        ]}
      />
    );

    expect(screen.getByText('Considered but not recommended')).toBeInTheDocument();
    expect(screen.getByText(/The database is down/)).toBeInTheDocument();
  });
});

describe('Why nothing was recommended', () => {
  it('explains that no action was available when no target was ready', () => {
    renderIncident({ assessment: { rootCauseCertainty: 'possible', nextSteps: [], consideredActions: [], offeredActions: [] },
      failureReason: 'No remediation action was available for this service and machine when the incident was analysed.' });

    expect(screen.getByText('No action was available')).toBeInTheDocument();
    expect(screen.getByText(/Turn on this app/)).toBeInTheDocument();
    expect(screen.getByText('No action available')).toBeInTheDocument(); // the status badge
  });

  it("shows the AI's reasons when it weighed the offered actions and chose none - without blaming the target", () => {
    renderIncident({ assessment: {
      rootCauseCertainty: 'possible', nextSteps: ['Investigate the KeyError.'], offeredActions: ['RestartApplication'],
      consideredActions: [{ action: 'RestartApplication', verdict: 'not_recommended',
        reason: 'A restart already cleared this once and the problem came back.' }]
    } });

    expect(screen.getByText('No action recommended')).toBeInTheDocument();
    expect(screen.getByText(/weighed the available actions/)).toBeInTheDocument();
    expect(screen.getByText(/the problem came back/)).toBeInTheDocument();
    expect(screen.queryByText(/Turn on this app/)).not.toBeInTheDocument();
    expect(screen.getByText('Investigate the KeyError.')).toBeInTheDocument();
  });

  it('never claims the AI ruled an action out when it gave no reasons', () => {
    renderIncident({ assessment: { rootCauseCertainty: 'unknown', nextSteps: [], consideredActions: [], offeredActions: ['RestartApplication'] } });

    expect(screen.getByText(/did not recommend/)).toBeInTheDocument();
    expect(screen.queryByText(/weighed the available actions/)).not.toBeInTheDocument();
  });
});
