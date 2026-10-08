import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import ApprovalPanel from './ApprovalPanel';
import IncidentDetail from './IncidentDetail';
import RemediationCenterPage from './RemediationCenterPage';
import { RecommendationPanel } from './AiPanels';
import { RemediationProgress } from './RemediationProgress';
import { IncidentStatus, RemediationStatus } from '../../types/incident';
import { classifyExecutionError, describeReadiness } from '../../lib/remediation';

const hooks = vi.hoisted(() => ({
  feed: null,
  details: null,
  actions: null
}));

vi.mock('../../hooks/useIncidents', () => ({
  useIncidents: () => hooks.feed,
  useIncidentDetails: () => hooks.details,
  useIncidentActions: () => hooks.actions
}));

const restartAction = {
  id: 'a1',
  actionKey: 'ACT-0001',
  actionType: 'RestartService',
  reason: 'The worker stopped processing orders',
  expectedOutcome: 'Order throughput returns to baseline',
  riskLevel: 'Medium',
  status: RemediationStatus.AwaitingApproval,
  targetWindowsServiceName: 'OrdersWorker',
  targetHostName: 'kairon-host',
  targetId: 't1',
  targetReadiness: 'Ready'
};

describe('ApprovalPanel - what exactly will run', () => {
  it('shows the Windows service, machine, environment, reason, expected effect, risk and readiness', () => {
    render(<ApprovalPanel action={restartAction} environment="Production" onApprove={vi.fn()} onReject={vi.fn()} />);

    expect(screen.getByText('Restart the Windows service')).toBeInTheDocument();
    expect(screen.getByText('OrdersWorker')).toBeInTheDocument();
    expect(screen.getByText('kairon-host')).toBeInTheDocument();
    expect(screen.getByText('Production')).toBeInTheDocument();
    expect(screen.getByText(restartAction.reason)).toBeInTheDocument();
    expect(screen.getByText(restartAction.expectedOutcome)).toBeInTheDocument();
    expect(screen.getByText('Medium risk')).toBeInTheDocument();
    expect(screen.getByText('✓ Ready')).toBeInTheDocument();
  });

  it('states the readiness problem plainly when the target is not ready', () => {
    render(<ApprovalPanel action={{ ...restartAction, targetReadiness: 'PermissionMissing' }} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.getByText(/✗ Needs Permission/)).toBeInTheDocument();
  });

  it('confirms with real wording - never a "demo environment"', async () => {
    render(<ApprovalPanel action={restartAction} environment="Production" onApprove={vi.fn()} onReject={vi.fn()} />);
    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'alice');
    await userEvent.click(screen.getByText('Approve and run'));

    expect(screen.getByText('Execute "Restart the Windows service" on the Windows service "OrdersWorker" on kairon-host (Production)?')).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/demo environment/);
  });

  it('catches a rejected approval instead of leaking an unhandled rejection, and shows the error', async () => {
    const onApprove = vi.fn().mockRejectedValue({ message: 'That action is not valid in the current state.' });
    render(<ApprovalPanel action={restartAction} onApprove={onApprove} onReject={vi.fn()} />);
    await userEvent.type(screen.getByLabelText(/Operator identity/i), 'alice');
    await userEvent.click(screen.getByText('Approve and run'));
    await userEvent.click(screen.getByText('Yes, execute it'));

    expect(await screen.findByRole('alert')).toHaveTextContent('That action is not valid in the current state.');
    expect(screen.getByText('Approve and run')).toBeInTheDocument();
  });
});

describe('RecommendationPanel policy notes', () => {
  it('shows the policy note for a registered tool too', () => {
    render(
      <RecommendationPanel
        recommendations={[{
          action: 'RestartService',
          reason: 'r',
          expectedOutcome: 'o',
          riskLevel: 'Medium',
          isRegisteredTool: true,
          policyNote: 'No enabled, ready remediation target authorizes this operation for this service and machine.'
        }]}
      />
    );
    expect(screen.getByText(/No enabled, ready remediation target authorizes this operation/)).toBeInTheDocument();
  });
});

describe('IncidentDetail when nothing is approvable', () => {
  it('shows the failure reason even though a diagnosis exists', () => {
    const incident = {
      id: 'i1',
      incidentKey: 'INC-0001',
      title: 'Orders degraded',
      service: 'OrdersService',
      environment: 'Production',
      status: IncidentStatus.RecommendationReady,
      severity: 'High',
      detectedAt: '2026-08-25T12:00:00Z',
      diagnosis: { summary: 's', rootCause: 'worker hung', confidence: 0.8 },
      failureReason: 'No enabled, ready remediation target authorizes RestartService for OrdersService.',
      actions: [],
      recommendations: [],
      timeline: []
    };
    render(
      <IncidentDetail
        query={{ state: 'success', data: incident, isLoading: false, isError: false, isEmpty: false, isSuccess: true, reload: vi.fn() }}
        actions={{ approve: vi.fn(), reject: vi.fn(), investigate: vi.fn(), busyActionId: null, actionError: null }}
      />
    );
    expect(screen.getByText('No action can be approved')).toBeInTheDocument();
    expect(screen.getByText(incident.failureReason)).toBeInTheDocument();
  });
});

describe('RemediationProgress', () => {
  it('shows an approved action that has not started yet instead of dropping it', () => {
    render(
      <RemediationProgress
        incidentStatus={IncidentStatus.Remediating}
        actions={[{ ...restartAction, status: RemediationStatus.Approved, approvedBy: 'alice', approvedAt: '2026-08-25T12:01:00Z' }]}
      />
    );
    expect(screen.getByText('Approved - waiting to start.')).toBeInTheDocument();
    expect(screen.getByText('OrdersWorker')).toBeInTheDocument();
    expect(screen.queryByText('Resolved')).toBeInTheDocument(); // the pending final step, not a success claim
    expect(screen.getByText('Resolved').closest('li')).toHaveClass('step-pending');
  });

  it('shows a policy-rejected action with its policy decision', () => {
    render(
      <RemediationProgress
        incidentStatus={IncidentStatus.Failed}
        actions={[{ ...restartAction, status: RemediationStatus.PolicyRejected, policyDecision: 'denied: no ready target' }]}
      />
    );
    expect(screen.getByText(/Blocked by policy: denied: no ready target/)).toBeInTheDocument();
  });

  it('explains a classified failure in plain language, keeping the raw error under details', () => {
    render(
      <RemediationProgress
        incidentStatus={IncidentStatus.Failed}
        actions={[{
          ...restartAction,
          status: RemediationStatus.Failed,
          approvedAt: '2026-08-25T12:01:00Z',
          startedAt: '2026-08-25T12:01:01Z',
          executionError: 'PermissionMissing: Access is denied (5) opening OrdersWorker.'
        }]}
      />
    );
    expect(screen.getByText(/Failed: Windows permission missing/)).toBeInTheDocument();
    expect(screen.getByText('Technical details')).toBeInTheDocument();
    expect(screen.getByText('PermissionMissing: Access is denied (5) opening OrdersWorker.')).toBeInTheDocument();
    expect(screen.getAllByText('Failed').some((el) => el.closest('li')?.classList.contains('step-failed'))).toBe(true);
  });

  it('only shows Resolved as done when the backend resolved the incident', () => {
    const executed = { ...restartAction, status: RemediationStatus.Executed, approvedAt: 'x', startedAt: '2026-08-25T12:01:01Z', completedAt: '2026-08-25T12:01:03Z' };
    const { rerender } = render(<RemediationProgress incidentStatus={IncidentStatus.Verifying} actions={[executed]} />);
    expect(screen.getByText('Resolved').closest('li')).toHaveClass('step-pending');
    rerender(<RemediationProgress incidentStatus={IncidentStatus.Resolved} actions={[executed]} />);
    expect(screen.getByText('Resolved').closest('li')).toHaveClass('step-done');
  });
});

describe('remediation vocabulary', () => {
  it('classifies prefixed execution errors and leaves unknown ones untouched', () => {
    expect(classifyExecutionError('DependentServicesRunning: Spooler has dependents')).toMatchObject({
      code: 'DependentServicesRunning',
      detail: 'Spooler has dependents'
    });
    expect(classifyExecutionError('something odd')).toMatchObject({ code: null, headline: null, detail: 'something odd' });
    expect(classifyExecutionError(null)).toBeNull();
  });

  it('maps readiness to human labels', () => {
    expect(describeReadiness('StaleTarget').label).toBe('Needs re-confirmation');
    expect(describeReadiness('RemoteNotSupported').label).toBe('Blocked');
    expect(describeReadiness('SomethingNew').label).toBe('Something New');
  });
});

describe('RemediationCenterPage bulk approval', () => {
  const pending = (id, incidentId, incidentKey) => ({
    ...restartAction,
    id,
    actionKey: `ACT-${id}`,
    status: RemediationStatus.AwaitingApproval,
    approvedAt: null,
    completedAt: null,
    incidentId,
    incidentKey
  });

  beforeEach(() => {
    hooks.feed = { state: 'success', data: [{ id: 'i1' }], isLoading: false, isError: false, isEmpty: false, reload: vi.fn() };
    hooks.details = {
      data: [
        { id: 'i1', incidentKey: 'INC-0001', actions: [pending('a1', 'i1', 'INC-0001'), pending('a2', 'i1', 'INC-0001')] },
        { id: 'i2', incidentKey: 'INC-0002', actions: [pending('b1', 'i2', 'INC-0002')] },
        { id: 'i3', incidentKey: 'INC-0003', actions: [pending('c1', 'i3', 'INC-0003')] }
      ],
      reload: vi.fn()
    };
    hooks.actions = { approve: vi.fn(), reject: vi.fn(), actionError: null };
  });

  it('approves each action on its own, keeps going after a failure, skips same-incident siblings and reports every item', async () => {
    const user = userEvent.setup();
    hooks.actions.approve.mockImplementation((incidentId) =>
      incidentId === 'i2' ? Promise.reject({ message: 'Target is not ready.' }) : Promise.resolve({})
    );
    render(<RemediationCenterPage />);

    await user.click(screen.getByLabelText('Select all pending actions'));
    await user.type(screen.getByLabelText('Operator identity'), 'alice');
    await user.click(screen.getByText('Approve selected'));
    await user.click(screen.getByText('Yes, execute all'));

    await waitFor(() => expect(screen.getByText(/Bulk approval: 2 succeeded, 1 failed, 1 skipped/)).toBeInTheDocument());
    // One approval per incident; i3 was still attempted after i2 failed.
    expect(hooks.actions.approve).toHaveBeenCalledTimes(3);
    expect(hooks.actions.approve).toHaveBeenCalledWith('i3', 'c1', 'alice', null);
    expect(hooks.actions.approve).not.toHaveBeenCalledWith('i1', 'a2', expect.anything(), expect.anything());
    expect(screen.getByText(/Target is not ready\./)).toBeInTheDocument();
    expect(screen.getByText(/Skipped - another action for the same incident was approved/)).toBeInTheDocument();
  });
});
