import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ToastProvider } from '../Toast';
import RemediationTargetsPage from './RemediationTargetsPage';
import { remediationTargetsApi, sdkApi, agentApi } from '../../api';

vi.mock('../../api', () => ({
  remediationTargetsApi: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    disable: vi.fn(),
    enable: vi.fn(),
    validate: vi.fn()
  },
  sdkApi: { listProjects: vi.fn(), listCredentials: vi.fn() },
  agentApi: { getMachines: vi.fn() }
}));

const project = { id: 'proj-1', name: 'Orders', activeCredentials: 1 };
const machine = { id: 'machine-1', hostName: 'enrolled-host', operatingSystem: 'Windows Server 2022' };
// A decoy secret-shaped field: the page must never render this even if a backend response
// somehow carried one, and this mock also proves credentials only ever expose keyPrefix/name.
const credential = { id: 'cred-1', name: 'prod-cred', keyPrefix: 'krn_abc123', revokedAt: null, apiKey: 'krn_should_never_render_this_secret' };

const target = {
  id: 'target-1',
  projectId: 'proj-1',
  projectName: 'Orders',
  environment: 'Production',
  service: 'OrderProcessingService',
  machineId: 'machine-1',
  machineHostName: 'enrolled-host',
  telemetryCredentialId: 'cred-1',
  telemetryCredentialName: 'prod-cred',
  expectedHostName: 'enrolled-host',
  windowsServiceName: 'ScopedService',
  allowedOperations: ['RestartService', 'RunHealthCheck'],
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z'
};

function renderPage(props = {}) {
  return render(
    <ToastProvider>
      <RemediationTargetsPage {...props} />
    </ToastProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  sdkApi.listProjects.mockResolvedValue([project]);
  sdkApi.listCredentials.mockResolvedValue([credential]);
  agentApi.getMachines.mockResolvedValue([machine]);
});

describe('RemediationTargetsPage', () => {
  it('shows a loading state before the list resolves', async () => {
    let resolveList;
    remediationTargetsApi.list.mockReturnValue(new Promise((resolve) => { resolveList = resolve; }));
    renderPage();
    expect(screen.getByText('Loading remediation targets...')).toBeInTheDocument();
    resolveList([]);
    await waitFor(() => expect(screen.queryByText('Loading remediation targets...')).not.toBeInTheDocument());
  });

  it('loads and displays the target list', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    expect(await screen.findByText('OrderProcessingService')).toBeInTheDocument();
    expect(screen.getByText('Orders')).toBeInTheDocument();
    expect(screen.getByText('enrolled-host')).toBeInTheDocument();
    expect(screen.getByText('ScopedService')).toBeInTheDocument();
    expect(screen.getByText('RestartService, RunHealthCheck')).toBeInTheDocument();
    expect(screen.getByText('Enabled')).toBeInTheDocument();
  });

  it('shows an empty state with a create action when there are no targets', async () => {
    remediationTargetsApi.list.mockResolvedValue([]);
    renderPage();
    expect(await screen.findByText('No remediation targets yet')).toBeInTheDocument();
  });

  it('shows a retry option when the list fails to load', async () => {
    remediationTargetsApi.list.mockRejectedValue({ message: 'Remediation targets are unavailable.' });
    renderPage();
    expect(await screen.findByText('Remediation targets are unavailable.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('never renders a credential secret anywhere on the page', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    await screen.findByText('OrderProcessingService');
    await userEvent.click(screen.getByRole('button', { name: '+ New Target' }));
    await userEvent.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(sdkApi.listCredentials).toHaveBeenCalledWith('proj-1'));
    expect(screen.queryByText(/krn_should_never_render_this_secret/)).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain('krn_should_never_render_this_secret');
  });

  it('creates a target with the exact payload the backend expects', async () => {
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.create.mockResolvedValue({ ...target });
    renderPage();
    await screen.findByText('No remediation targets yet');

    await userEvent.click(screen.getAllByRole('button', { name: '+ New Target' })[0]);
    await userEvent.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await userEvent.selectOptions(screen.getByLabelText('Environment'), 'Production');
    await userEvent.type(screen.getByLabelText('Logical Service'), 'OrderProcessingService');
    await userEvent.selectOptions(screen.getByLabelText('Machine'), 'machine-1');
    await userEvent.type(screen.getByLabelText('Windows Service Name'), 'ScopedService');
    await screen.findByRole('option', { name: /prod-cred/ });
    await userEvent.selectOptions(screen.getByLabelText('Telemetry Credential'), 'cred-1');
    await userEvent.click(screen.getByRole('checkbox', { name: 'RestartService' }));

    expect(screen.getByText(/Expected host name:/)).toHaveTextContent('enrolled-host');

    await userEvent.click(screen.getByRole('button', { name: 'Create target' }));

    await waitFor(() => expect(remediationTargetsApi.create).toHaveBeenCalledWith({
      projectId: 'proj-1',
      environment: 'Production',
      service: 'OrderProcessingService',
      machineId: 'machine-1',
      telemetryCredentialId: 'cred-1',
      expectedHostName: 'enrolled-host',
      windowsServiceName: 'ScopedService',
      allowedOperations: ['RestartService'],
      enabled: true
    }));
  });

  it('displays a backend validation failure clearly without persisting', async () => {
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.create.mockRejectedValue({ status: 422, code: 'invalid-target', message: 'Windows service name is not a valid Windows service name.' });
    renderPage();
    await screen.findByText('No remediation targets yet');

    await userEvent.click(screen.getAllByRole('button', { name: '+ New Target' })[0]);
    await userEvent.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await userEvent.type(screen.getByLabelText('Logical Service'), 'Svc');
    await userEvent.selectOptions(screen.getByLabelText('Machine'), 'machine-1');
    await userEvent.type(screen.getByLabelText('Windows Service Name'), 'bad name!');
    await screen.findByRole('option', { name: /prod-cred/ });
    await userEvent.selectOptions(screen.getByLabelText('Telemetry Credential'), 'cred-1');
    await userEvent.click(screen.getByRole('checkbox', { name: 'RestartService' }));
    await userEvent.click(screen.getByRole('button', { name: 'Create target' }));

    expect(await screen.findByText('Windows service name is not a valid Windows service name.')).toBeInTheDocument();
    // The form is not closed on a rejected save.
    expect(screen.getByRole('button', { name: 'Create target' })).toBeInTheDocument();
  });

  it('runs a preflight check without persisting anything', async () => {
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.validate.mockResolvedValue({ valid: false, errors: ['Machine does not exist.'] });
    renderPage();
    await screen.findByText('No remediation targets yet');
    await userEvent.click(screen.getAllByRole('button', { name: '+ New Target' })[0]);

    await userEvent.click(screen.getByRole('button', { name: 'Check for errors' }));

    expect(await screen.findByText('Machine does not exist.')).toBeInTheDocument();
    expect(remediationTargetsApi.create).not.toHaveBeenCalled();
  });

  it('edits an existing target, sending expectedUpdatedAt for optimistic concurrency', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.update.mockResolvedValue({ ...target, windowsServiceName: 'RenamedService' });
    renderPage();
    await screen.findByText('OrderProcessingService');

    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));
    expect(screen.getByLabelText('Windows Service Name')).toHaveValue('ScopedService');

    await userEvent.clear(screen.getByLabelText('Windows Service Name'));
    await userEvent.type(screen.getByLabelText('Windows Service Name'), 'RenamedService');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(remediationTargetsApi.update).toHaveBeenCalledWith('target-1', expect.objectContaining({
      windowsServiceName: 'RenamedService',
      expectedUpdatedAt: '2026-01-02T00:00:00Z'
    })));
  });

  it('shows a clear message and does not overwrite on a stale-update conflict', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.update.mockRejectedValue({ status: 409, code: 'stale-update', message: 'stale' });
    renderPage();
    await screen.findByText('OrderProcessingService');

    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(
      'This target was changed by another operator. Reload the latest version before editing it again.'
    )).toBeInTheDocument();
  });

  it('requires confirmation before disabling a target', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.disable.mockResolvedValue({});
    renderPage();
    await screen.findByText('OrderProcessingService');

    await userEvent.click(screen.getByRole('button', { name: 'Disable' }));
    expect(remediationTargetsApi.disable).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Confirm disable' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Confirm disable' }));
    await waitFor(() => expect(remediationTargetsApi.disable).toHaveBeenCalledWith('target-1'));
  });

  it('cancelling the disable confirmation does not call the API', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    await screen.findByText('OrderProcessingService');

    await userEvent.click(screen.getByRole('button', { name: 'Disable' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(remediationTargetsApi.disable).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Confirm disable' })).not.toBeInTheDocument();
  });

  it('enables a disabled target and surfaces a validation failure clearly if it cannot be enabled', async () => {
    const disabled = { ...target, enabled: false };
    remediationTargetsApi.list.mockResolvedValue([disabled]);
    remediationTargetsApi.enable.mockRejectedValue({ message: 'This target cannot be enabled: Telemetry credential does not exist, is revoked, or does not belong to this project.' });
    renderPage();
    await screen.findByText('OrderProcessingService');

    await userEvent.click(screen.getByRole('button', { name: 'Enable' }));
    await waitFor(() => expect(remediationTargetsApi.enable).toHaveBeenCalledWith('target-1'));
    expect(await screen.findByText(/This target cannot be enabled/)).toBeInTheDocument();
  });

  it('opens the create form with the machine preselected via the machine-page shortcut', async () => {
    remediationTargetsApi.list.mockResolvedValue([]);
    renderPage({ preselectMachineId: 'machine-1' });
    await screen.findByText('No remediation targets yet');

    expect(await screen.findByLabelText('Machine')).toHaveValue('machine-1');
    expect(screen.getByText(/Expected host name:/)).toHaveTextContent('enrolled-host');
  });
});
