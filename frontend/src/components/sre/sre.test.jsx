import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import IncidentFeed from './IncidentFeed';
import IncidentDetail from './IncidentDetail';
import ApprovalPanel from './ApprovalPanel';
import { AiInvestigationPanel, PredictionPanel, RecommendationPanel } from './AiPanels';
import { RemediationProgress, VerificationPanel } from './RemediationProgress';
import { AsyncView, ErrorState } from './StateViews';
import { IncidentStatus, RemediationStatus, VerificationStatus } from '../../types/incident';

const query = (overrides = {}) => ({
  state: 'success',
  data: null,
  error: null,
  lastUpdated: null,
  isLoading: false,
  isError: false,
  isEmpty: false,
  isSuccess: true,
  reload: vi.fn(),
  ...overrides
});

const noopActions = {
  approve: vi.fn(),
  reject: vi.fn(),
  cancel: vi.fn(),
  investigate: vi.fn(),
  busyActionId: null,
  actionError: null
};

const sampleIncident = (overrides = {}) => ({
  id: 'i1',
  incidentKey: 'INC-0001',
  title: 'OrderProcessingService Service Degradation',
  application: 'Kairon.DemoApp',
  service: 'OrderProcessingService',
  environment: 'Demo',
  severity: 'High',
  status: IncidentStatus.AwaitingApproval,
  affectedComponent: 'OrderProcessingService',
  affectedEndpoint: '/api/orders/process',
  detectedAt: '2026-08-25T12:00:00Z',
  updatedAt: '2026-08-25T12:05:00Z',
  signalCount: 3,
  symptoms: ['Retry rate 90/min', 'CPU 94%'],
  shortDescription: 'Retry storm on order processing',
  needsApproval: true,
  correlatedSignals: [
    { rule: 'retry-storm', metric: 'retries', observed: 90, threshold: 30, unit: '/min', severity: 'High' }
  ],
  telemetryReferences: ['t1'],
  recommendations: [],
  actions: [],
  verifications: [],
  timeline: [],
  ...overrides
});

describe('IncidentFeed', () => {
  it('renders incidents with severity and status', () => {
    render(
      <IncidentFeed
        query={query({ data: [sampleIncident()] })}
        incidents={[sampleIncident()]}
        selectedId={null}
        onSelect={vi.fn()}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    expect(screen.getByText('INC-0001')).toBeInTheDocument();
    expect(screen.getByText('High')).toBeInTheDocument();
    expect(screen.getByText('Awaiting Approval')).toBeInTheDocument();
  });

  it('flags incidents needing approval', () => {
    render(
      <IncidentFeed
        query={query({ data: [sampleIncident()] })}
        incidents={[sampleIncident()]}
        selectedId={null}
        onSelect={vi.fn()}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    expect(screen.getByText('Approval needed')).toBeInTheDocument();
  });

  it('shows an empty state on a healthy system', () => {
    render(
      <IncidentFeed
        query={query({ isEmpty: true, data: [] })}
        incidents={[]}
        selectedId={null}
        onSelect={vi.fn()}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    expect(screen.getByText('No incidents')).toBeInTheDocument();
  });

  it('shows a loading state', () => {
    render(
      <IncidentFeed
        query={query({ isLoading: true, isSuccess: false })}
        incidents={[]}
        selectedId={null}
        onSelect={vi.fn()}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    expect(screen.getByText('Loading incidents...')).toBeInTheDocument();
  });

  it('shows an operator-facing message when the backend is unreachable', () => {
    render(
      <IncidentFeed
        query={query({
          isError: true,
          isSuccess: false,
          error: { kind: 'offline', message: 'Cannot reach the Kairon backend.' }
        })}
        incidents={[]}
        selectedId={null}
        onSelect={vi.fn()}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    expect(screen.getByText('Backend unreachable')).toBeInTheDocument();
  });

  it('selects an incident when clicked', async () => {
    const onSelect = vi.fn();
    render(
      <IncidentFeed
        query={query({ data: [sampleIncident()] })}
        incidents={[sampleIncident()]}
        selectedId={null}
        onSelect={onSelect}
        filters={{ status: 'active' }}
        onFilterChange={vi.fn()}
      />
    );

    await userEvent.click(screen.getByText('OrderProcessingService Service Degradation'));

    expect(onSelect).toHaveBeenCalledWith('i1');
  });
});

describe('AI panels', () => {
  const diagnosis = {
    summary: 'Service is degraded',
    rootCause: 'Controlled retry loop causing repeated downstream requests.',
    contributingFactors: ['Retry rate 90/min'],
    evidence: ['retries 90/min vs threshold 30/min'],
    confidence: 0.92,
    provider: 'gemini',
    model: 'gemini-2.5-flash-lite'
  };

  it('renders the diagnosis', () => {
    render(<AiInvestigationPanel diagnosis={diagnosis} />);

    expect(screen.getByText(diagnosis.rootCause)).toBeInTheDocument();
  });

  it('presents confidence as an estimate, never as certainty', () => {
    render(<AiInvestigationPanel diagnosis={diagnosis} />);

    expect(screen.getByText(/92% \(model estimate\)/)).toBeInTheDocument();
  });

  it('marks AI content as AI-derived', () => {
    render(<AiInvestigationPanel diagnosis={diagnosis} />);

    expect(screen.getByText('AI estimate')).toBeInTheDocument();
    expect(screen.getByText(/model-generated hypothesis/i)).toBeInTheDocument();
  });

  it('offers a retry when the AI was unavailable', async () => {
    const onRetry = vi.fn();
    render(
      <AiInvestigationPanel diagnosis={null} failureReason="AI service is down" onRetry={onRetry} />
    );

    expect(screen.getByText('AI service is down')).toBeInTheDocument();

    await userEvent.click(screen.getByText('Retry AI investigation'));
    expect(onRetry).toHaveBeenCalled();
  });

  it('labels the prediction as a prediction', () => {
    render(
      <PredictionPanel prediction={{ predictedFailure: 'Backlog grows', estimatedRisk: 'high' }} />
    );

    expect(screen.getByText('AI prediction')).toBeInTheDocument();
    expect(screen.getByText(/not an observed outcome/i)).toBeInTheDocument();
  });

  it('makes clear that a recommendation has not been executed', () => {
    render(
      <RecommendationPanel
        recommendations={[
          {
            action: 'DisableDemoRetryLoop',
            reason: 'leading signal',
            expectedOutcome: 'retries drop',
            riskLevel: 'Low',
            isRegisteredTool: true
          }
        ]}
      />
    );

    expect(screen.getByText(/Nothing runs until an operator approves/i)).toBeInTheDocument();
  });

  it('shows why an unregistered recommendation cannot run', () => {
    render(
      <RecommendationPanel
        recommendations={[
          {
            action: 'rm -rf /',
            reason: 'malicious',
            expectedOutcome: 'bad',
            riskLevel: 'Low',
            isRegisteredTool: false,
            policyNote: 'not a registered remediation tool'
          }
        ]}
      />
    );

    expect(screen.getByText(/Not executable/i)).toBeInTheDocument();
  });
});

describe('ApprovalPanel', () => {
  const action = {
    id: 'a1',
    actionKey: 'ACT-0001',
    actionType: 'DisableDemoRetryLoop',
    reason: 'Retry volume is the leading signal',
    expectedOutcome: 'Retries return to baseline',
    riskLevel: 'Low',
    policyDecision: 'allowed: registered and permitted'
  };

  it('shows the action, reason, outcome and risk', () => {
    render(<ApprovalPanel action={action} onApprove={vi.fn()} onReject={vi.fn()} />);

    expect(screen.getByText('DisableDemoRetryLoop')).toBeInTheDocument();
    expect(screen.getByText(action.reason)).toBeInTheDocument();
    expect(screen.getByText(action.expectedOutcome)).toBeInTheDocument();
    expect(screen.getByText('Low risk')).toBeInTheDocument();
  });

  it('refuses to enable approval without an operator identity', () => {
    render(<ApprovalPanel action={action} onApprove={vi.fn()} onReject={vi.fn()} />);

    expect(screen.getByText('Approve and execute')).toBeDisabled();
    expect(screen.getByText('Reject')).toBeDisabled();
  });

  it('never approves in a single click', async () => {
    // Executing a remediation must be deliberate (frontend PRD section 10).
    const onApprove = vi.fn();
    render(<ApprovalPanel action={action} onApprove={onApprove} onReject={vi.fn()} />);

    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'alice');
    await userEvent.click(screen.getByText('Approve and execute'));

    expect(onApprove).not.toHaveBeenCalled();
    expect(screen.getByText(/Execute DisableDemoRetryLoop/i)).toBeInTheDocument();
  });

  it('approves with the operator identity after confirmation', async () => {
    const onApprove = vi.fn();
    render(<ApprovalPanel action={action} onApprove={onApprove} onReject={vi.fn()} />);

    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'alice');
    await userEvent.click(screen.getByText('Approve and execute'));
    await userEvent.click(screen.getByText('Yes, execute it'));

    await waitFor(() => expect(onApprove).toHaveBeenCalledWith('a1', 'alice', null));
  });

  it('rejects with a reason', async () => {
    const onReject = vi.fn();
    render(<ApprovalPanel action={action} onApprove={vi.fn()} onReject={onReject} />);

    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'bob');
    await userEvent.type(screen.getByLabelText(/Note/i), 'not now');
    await userEvent.click(screen.getByText('Reject'));
    await userEvent.click(screen.getByText('Yes, reject it'));

    await waitFor(() => expect(onReject).toHaveBeenCalledWith('a1', 'bob', 'not now'));
  });

  it('lets the operator back out of a confirmation', async () => {
    const onApprove = vi.fn();
    render(<ApprovalPanel action={action} onApprove={onApprove} onReject={vi.fn()} />);

    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'alice');
    await userEvent.click(screen.getByText('Approve and execute'));
    await userEvent.click(screen.getByText('Cancel'));

    expect(onApprove).not.toHaveBeenCalled();
    expect(screen.getByText('Approve and execute')).toBeInTheDocument();
  });

  it('surfaces a failed approval to the operator', () => {
    render(
      <ApprovalPanel
        action={action}
        onApprove={vi.fn()}
        onReject={vi.fn()}
        error={{ message: 'That action is not valid in the current state.' }}
      />
    );

    expect(screen.getByText('That action is not valid in the current state.')).toBeInTheDocument();
  });
});

describe('RemediationProgress and VerificationPanel', () => {
  it('shows progress through the remediation steps', () => {
    render(
      <RemediationProgress
        incidentStatus={IncidentStatus.Verifying}
        actions={[
          {
            id: 'a1',
            actionKey: 'ACT-0001',
            actionType: 'DisableDemoRetryLoop',
            status: RemediationStatus.Executed,
            approvedBy: 'alice',
            approvedAt: '2026-08-25T12:01:00Z',
            startedAt: '2026-08-25T12:01:01Z',
            completedAt: '2026-08-25T12:01:03Z',
            executionResult: 'Retry loop disabled.'
          }
        ]}
      />
    );

    expect(screen.getByText('Retry loop disabled.')).toBeInTheDocument();
    expect(screen.getByText(/alice/)).toBeInTheDocument();
  });

  it('states a remediation failure plainly', () => {
    render(
      <RemediationProgress
        incidentStatus={IncidentStatus.Failed}
        actions={[
          {
            id: 'a1',
            actionKey: 'ACT-0001',
            actionType: 'RestartDemoService',
            status: RemediationStatus.Failed,
            executionError: 'demo environment refused the command'
          }
        ]}
      />
    );

    expect(screen.getByText(/Failed: demo environment refused the command/)).toBeInTheDocument();
  });

  it('renders a before/after comparison', () => {
    render(
      <VerificationPanel
        verification={{
          id: 'v1',
          status: VerificationStatus.Passed,
          summary: 'Recovery confirmed',
          recoveryScore: 1,
          comparisons: [
            { metric: 'cpu', before: 94, after: 22, unit: '%', improved: true, meetsThreshold: true, threshold: 80 }
          ]
        }}
      />
    );

    expect(screen.getByText('94%')).toBeInTheDocument();
    expect(screen.getByText('22%')).toBeInTheDocument();
    expect(screen.getByText('-77%')).toBeInTheDocument();
    expect(screen.getByText('Within threshold')).toBeInTheDocument();
  });

  it('shows an unreported metric as unknown, not as a breach', () => {
    // A service that stopped reporting a metric has not failed to recover it.
    render(
      <VerificationPanel
        verification={{
          id: 'v1',
          status: VerificationStatus.Passed,
          summary: 'Recovery confirmed',
          recoveryScore: 1,
          comparisons: [
            { metric: 'retries', before: 75, after: null, unit: '/min', improved: false, meetsThreshold: false, threshold: 30 }
          ]
        }}
      />
    );

    expect(screen.getByText('Not reported')).toBeInTheDocument();
    expect(screen.queryByText('Still breaching')).not.toBeInTheDocument();
  });

  it('warns when recovery could not be confirmed', () => {
    render(
      <VerificationPanel
        verification={{
          id: 'v1',
          status: VerificationStatus.Inconclusive,
          summary: 'No telemetry after remediation',
          recoveryScore: 0,
          comparisons: []
        }}
      />
    );

    expect(screen.getByText(/could not be confirmed/i)).toBeInTheDocument();
  });
});

describe('IncidentDetail', () => {
  it('renders the full investigation', () => {
    render(<IncidentDetail query={query({ data: sampleIncident() })} actions={noopActions} />);

    expect(screen.getByText('OrderProcessingService Service Degradation')).toBeInTheDocument();
    expect(screen.getByText('Symptoms')).toBeInTheDocument();
    expect(screen.getByText('Measured telemetry')).toBeInTheDocument();
  });

  it('tags a KAIRON Agent-sourced signal but not a metric-threshold one', () => {
    // docs/OBSERVABILITY_MIGRATION.md: the Symptoms table already renders any correlated signal
    // generically - this only checks the small "Agent" marker that distinguishes a signal
    // collected by the Agent (log tailer/process watcher) from one collected by the SDK.
    render(
      <IncidentDetail
        query={query({
          data: sampleIncident({
            correlatedSignals: [
              { rule: 'retry-storm', metric: 'retries', observed: 90, threshold: 30, unit: '/min', severity: 'High' },
              { rule: 'log-pattern-match', metric: 'logPattern', observed: 2, threshold: 2, unit: ' occurrences', severity: 'High' }
            ]
          })
        })}
        actions={noopActions}
      />
    );

    expect(screen.getAllByText('Agent')).toHaveLength(1);
  });

  it('shows the Resolved banner only when the backend says so', () => {
    // Scoped to the resolution banner: the lifecycle rail always carries a "Resolved" label as a
    // pending stage, which is correct and must not be confused with a resolution claim.
    const resolvedBanner = () => document.querySelector('.resolution-good');

    const { rerender } = render(
      <IncidentDetail
        query={query({ data: sampleIncident({ status: IncidentStatus.Verifying }) })}
        actions={noopActions}
      />
    );

    expect(resolvedBanner()).toBeNull();

    rerender(
      <IncidentDetail
        query={query({
          data: sampleIncident({ status: IncidentStatus.Resolved, resolvedAt: '2026-08-25T12:10:00Z' })
        })}
        actions={noopActions}
      />
    );

    expect(resolvedBanner()).toBeInTheDocument();
    expect(resolvedBanner().textContent).toMatch(/Backend verification confirmed recovery/);
  });

  it('shows a verification failure banner', () => {
    render(
      <IncidentDetail
        query={query({
          data: sampleIncident({
            status: IncidentStatus.AwaitingApproval,
            verifications: [
              {
                id: 'v1',
                status: VerificationStatus.Failed,
                summary: 'Metrics still breaching',
                startedAt: '2026-08-25T12:08:00Z',
                comparisons: []
              }
            ]
          })
        })}
        actions={noopActions}
      />
    );

    expect(screen.getByText('Verification failed')).toBeInTheDocument();
  });

  it('shows the approval panel when an action awaits a decision', () => {
    render(
      <IncidentDetail
        query={query({
          data: sampleIncident({
            actions: [
              {
                id: 'a1',
                actionKey: 'ACT-0001',
                actionType: 'DisableDemoRetryLoop',
                status: RemediationStatus.AwaitingApproval,
                riskLevel: 'Low',
                reason: 'r',
                expectedOutcome: 'o'
              }
            ]
          })
        })}
        actions={noopActions}
      />
    );

    expect(screen.getByText('Operator Approval Required')).toBeInTheDocument();
  });

  it('prompts to pick an incident when none is selected', () => {
    render(<IncidentDetail query={query({ isEmpty: true })} actions={noopActions} />);

    expect(screen.getByText('Select an incident')).toBeInTheDocument();
  });
});

describe('ErrorState', () => {
  it('never renders a stack trace', () => {
    const { container } = render(
      <ErrorState error={{ kind: 'server', message: 'The request failed.' }} />
    );

    expect(container.textContent).not.toMatch(/at [A-Z]\w+\./);
    expect(screen.getByText('The request failed.')).toBeInTheDocument();
  });

  it('offers a retry', async () => {
    const onRetry = vi.fn();
    render(<ErrorState error={{ message: 'nope' }} onRetry={onRetry} />);

    await userEvent.click(screen.getByText('Retry'));
    expect(onRetry).toHaveBeenCalled();
  });
});

describe('AsyncView', () => {
  it('prefers loading over empty when there is no data yet', () => {
    render(
      <AsyncView query={query({ isLoading: true, isSuccess: false })} loadingLabel="Loading...">
        {() => <div>content</div>}
      </AsyncView>
    );

    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('keeps showing data when a background refresh fails', () => {
    render(
      <AsyncView query={query({ isError: true, data: [1], error: { message: 'blip' } })}>
        {() => <div>content</div>}
      </AsyncView>
    );

    expect(screen.getByText('content')).toBeInTheDocument();
  });
});
