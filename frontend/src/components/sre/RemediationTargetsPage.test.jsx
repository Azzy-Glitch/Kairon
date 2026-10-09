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
    validate: vi.fn(),
    preflight: vi.fn(),
    targetPreflight: vi.fn(),
    machineServices: vi.fn()
  },
  sdkApi: { listProjects: vi.fn(), listCredentials: vi.fn(), listApplications: vi.fn() },
  agentApi: { getMachines: vi.fn() }
}));

// Several tests drive the full six-step wizard one interaction at a time, the way a real user
// does - a wider budget than vitest's 5s default for those, the global default stays untouched.
const WIZARD_TIMEOUT = 20000;

const project = { id: 'proj-1', name: 'Orders', activeCredentials: 1 };
const machine = { id: 'machine-1', hostName: 'enrolled-host', operatingSystem: 'Windows Server 2022', status: 'Online' };
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
  machineBindingStatus: 'PendingAgentConfirmation',
  expectedHostName: 'enrolled-host',
  windowsServiceName: 'ScopedService',
  serviceDisplayName: 'Scoped Orders Worker',
  serviceIdentityConfirmed: true,
  allowedOperations: ['RestartService', 'RunHealthCheck'],
  enabled: true,
  readiness: 'AwaitingAgentConfirmation',
  readinessDetail: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z'
};

const services = [
  { serviceName: 'ScopedService', displayName: 'Scoped Orders Worker', state: 'Running', eligible: true, eligibility: 'Eligible', eligibilityDetail: null },
  { serviceName: 'WinDefend', displayName: 'Microsoft Defender Antivirus', state: 'Running', eligible: false, eligibility: 'Denylisted', eligibilityDetail: 'Security-critical Windows service.' }
];

const passingPreflight = {
  readiness: 'Ready',
  canEnable: true,
  checks: [
    { key: 'service', label: 'Windows service exists', passed: true, blocking: true, detail: null, readiness: 'ServiceMissing' },
    { key: 'rights', label: 'KAIRON has the Windows rights it needs', passed: true, blocking: true, detail: null, readiness: 'PermissionMissing' }
  ],
  service: { serviceName: 'ScopedService', displayName: 'Scoped Orders Worker', state: 'Running', eligibility: 'Eligible' },
  requiredRights: ['Query'],
  missingRights: [],
  executorAccount: 'NT SERVICE\\Kairon',
  executorSid: 'S-1-5-80-1',
  fixCommand: null
};

const missingRightsPreflight = {
  ...passingPreflight,
  readiness: 'PermissionMissing',
  canEnable: false,
  checks: [
    passingPreflight.checks[0],
    { key: 'rights', label: 'KAIRON has the Windows rights it needs', passed: false, blocking: true, detail: 'Start and Stop are not granted.', readiness: 'PermissionMissing' }
  ],
  requiredRights: ['Query', 'Start', 'Stop'],
  missingRights: ['Start', 'Stop'],
  fixCommand: ".\\tools\\remediation\\Set-KaironServicePermission.ps1 -ServiceName 'ScopedService' -Sid 'S-1-5-80-1' -Rights Query,Start,Stop"
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
  sdkApi.listApplications.mockResolvedValue([{ id: 'app-1', service: 'OrderProcessingService', lastTelemetryAt: null }]);
  agentApi.getMachines.mockResolvedValue([machine]);
  remediationTargetsApi.machineServices.mockResolvedValue(services);
  remediationTargetsApi.preflight.mockResolvedValue(passingPreflight);
  remediationTargetsApi.targetPreflight.mockResolvedValue(passingPreflight);
});

/** Steps 1-3 of the wizard: project/service (credential auto-selected), machine (the only one is
 * preselected), Windows service picked from the real list. */
async function fillWizardToOperations(user) {
  await user.click(screen.getAllByRole('button', { name: '+ Configure Target' })[0]);
  await user.click(screen.getByRole('radio', { name: /A Windows service/ }));
  await user.selectOptions(await screen.findByLabelText('Project'), 'proj-1');
  await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
  await user.type(screen.getByLabelText('Logical service'), 'OrderProcessingService');
  await user.click(screen.getByRole('button', { name: 'Next' }));

  expect(await screen.findByRole('radio', { name: /enrolled-host/ })).toBeChecked();
  await user.click(screen.getByRole('button', { name: 'Next' }));

  await user.click(await screen.findByRole('radio', { name: /Scoped Orders Worker/ }));
  await user.click(screen.getByRole('button', { name: 'Next' }));
}

describe('RemediationTargetsPage list', () => {
  it('shows a loading state before the list resolves', async () => {
    let resolveList;
    remediationTargetsApi.list.mockReturnValue(new Promise((resolve) => { resolveList = resolve; }));
    renderPage();
    expect(screen.getByText('Loading remediation targets...')).toBeInTheDocument();
    resolveList([]);
    await waitFor(() => expect(screen.queryByText('Loading remediation targets...')).not.toBeInTheDocument());
  });

  it('shows each target in plain language, with no IDs in the main view', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    expect(await screen.findByText('Scoped Orders Worker')).toBeInTheDocument();
    expect(screen.getByText('ScopedService')).toBeInTheDocument();
    expect(screen.getByText('enrolled-host')).toBeInTheDocument();
    expect(screen.getByText('Production')).toBeInTheDocument();
    expect(screen.getByText('OrderProcessingService')).toBeInTheDocument();
    expect(screen.getByText('Waiting for app telemetry')).toBeInTheDocument();
    expect(screen.getByText('Restart the service, Run a health check')).toBeInTheDocument();
    for (const id of ['target-1', 'machine-1', 'cred-1', 'proj-1']) {
      expect(document.body.textContent).not.toContain(id);
    }
  });

  it.each([
    ['Ready', true, 'Ready'],
    ['PermissionMissing', true, 'Needs Permission'],
    ['ServiceMissing', true, 'Service Missing'],
    ['MachineOffline', true, 'Machine Offline'],
    ['StaleTarget', true, 'Needs re-confirmation'],
    ['ServiceIdentityChanged', true, 'Needs re-confirmation'],
    ['Denylisted', true, 'Blocked'],
    ['RemoteNotSupported', true, 'Blocked'],
    ['UnsupportedPlatform', true, 'Unsupported'],
    ['Ready', false, 'Disabled']
  ])('maps readiness %s (enabled=%s) to "%s"', async (readiness, enabled, label) => {
    remediationTargetsApi.list.mockResolvedValue([{ ...target, readiness, enabled }]);
    renderPage();
    expect(await screen.findByText(label)).toBeInTheDocument();
  });

  it('shows an empty state with a configure action when there are no targets', async () => {
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

  it('requires confirmation before disabling a target', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.disable.mockResolvedValue({});
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await userEvent.click(screen.getByRole('button', { name: 'Disable' }));
    expect(remediationTargetsApi.disable).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Confirm disable' }));
    await waitFor(() => expect(remediationTargetsApi.disable).toHaveBeenCalledWith('target-1'));
  });

  it('cancelling the disable confirmation does not call the API', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await userEvent.click(screen.getByRole('button', { name: 'Disable' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(remediationTargetsApi.disable).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Confirm disable' })).not.toBeInTheDocument();
  });

  it('enables a disabled target and surfaces a pre-flight failure clearly if it cannot be enabled', async () => {
    remediationTargetsApi.list.mockResolvedValue([{ ...target, enabled: false }]);
    remediationTargetsApi.enable.mockRejectedValue({ status: 422, message: 'This target cannot be enabled: KAIRON is missing the Start right on ScopedService.' });
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await userEvent.click(screen.getByRole('button', { name: 'Enable' }));
    await waitFor(() => expect(remediationTargetsApi.enable).toHaveBeenCalledWith('target-1'));
    expect(await screen.findByText(/missing the Start right/)).toBeInTheDocument();
  });

  it('View shows the saved-target pre-flight, including Agent confirmation, with IDs only under Advanced details', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.targetPreflight.mockResolvedValue({
      ...passingPreflight,
      readiness: 'AwaitingAgentConfirmation',
      checks: [
        ...passingPreflight.checks,
        { key: 'agent-proof', label: null, passed: false, blocking: false, detail: 'No Agent-confirmed telemetry yet.', readiness: 'AwaitingAgentConfirmation' }
      ],
      service: { ...passingPreflight.service, imagePath: 'C:\\Svc\\worker.exe', win32Error: 5 }
    });
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await userEvent.click(screen.getByRole('button', { name: 'View' }));
    await waitFor(() => expect(remediationTargetsApi.targetPreflight).toHaveBeenCalledWith('target-1'));
    expect(await screen.findByText('App telemetry confirmed by the Agent')).toBeInTheDocument();
    expect(screen.getByText('No Agent-confirmed telemetry yet.')).toBeInTheDocument();

    const advanced = screen.getByText('Advanced details').closest('details');
    expect(advanced).not.toHaveAttribute('open');
    expect(within(advanced).getByText('target-1')).toBeInTheDocument();
    expect(within(advanced).getByText('NT SERVICE\\Kairon')).toBeInTheDocument();
    expect(within(advanced).getByText('C:\\Svc\\worker.exe')).toBeInTheDocument();
    expect(within(advanced).getByText('5')).toBeInTheDocument();
  });
});

describe('Configure Target wizard', () => {
  it('never renders a credential secret anywhere on the page', async () => {
    remediationTargetsApi.list.mockResolvedValue([target]);
    renderPage();
    await screen.findByText('Scoped Orders Worker');
    await userEvent.click(screen.getByRole('button', { name: '+ Configure Target' }));
    await userEvent.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(sdkApi.listCredentials).toHaveBeenCalledWith('proj-1'));
    expect(document.body.textContent).not.toContain('krn_should_never_render_this_secret');
  });

  it('starts with safe defaults and never sends an incomplete request', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    renderPage();
    await screen.findByText('No remediation targets yet');
    await user.click(screen.getAllByRole('button', { name: '+ Configure Target' })[0]);

    expect(screen.getByLabelText('Environment')).toHaveValue('Development');
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    expect(remediationTargetsApi.preflight).not.toHaveBeenCalled();
    expect(remediationTargetsApi.validate).not.toHaveBeenCalled();
    expect(remediationTargetsApi.create).not.toHaveBeenCalled();
  });

  it('lists eligible services to pick from, with ineligible ones disabled and explained', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    renderPage();
    await screen.findByText('No remediation targets yet');
    await user.click(screen.getAllByRole('button', { name: '+ Configure Target' })[0]);
    await user.click(screen.getByRole('radio', { name: /A Windows service/ }));
    await user.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    await user.type(screen.getByLabelText('Logical service'), 'OrderProcessingService');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(remediationTargetsApi.machineServices).toHaveBeenCalledWith('machine-1'));
    expect(await screen.findByRole('radio', { name: /Scoped Orders Worker/ })).toBeEnabled();
    expect(screen.getByRole('radio', { name: /Microsoft Defender Antivirus/ })).toBeDisabled();
    expect(screen.getByText(/Security-critical Windows service\./)).toBeInTheDocument();

    await user.type(screen.getByLabelText('Search services'), 'nothing-matches');
    expect(screen.queryByRole('radio', { name: /Scoped Orders Worker/ })).not.toBeInTheDocument();
  }, WIZARD_TIMEOUT);

  it('explains plainly when the machine is not the KAIRON host', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.machineServices.mockRejectedValue({ status: 422, code: 'remote-not-supported', message: 'remote' });
    renderPage({ preselectMachineId: 'machine-1' });
    await user.click(await screen.findByRole('radio', { name: /A Windows service/ }));
    await user.selectOptions(await screen.findByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    await user.type(screen.getByLabelText('Logical service'), 'Svc');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('radio', { name: /enrolled-host/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText(/not the one running KAIRON/)).toBeInTheDocument();
  }, WIZARD_TIMEOUT);

  it('defaults to a health check only, marks Stop as high risk, runs the permission check automatically and enables', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.create.mockResolvedValue({ ...target });
    renderPage();
    await screen.findByText('No remediation targets yet');
    await fillWizardToOperations(user);

    expect(screen.getByRole('checkbox', { name: /Run a health check/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Restart the service/ })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Stop the service/ })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Stop the service/ }).closest('label')).toHaveTextContent('High risk');
    await user.click(screen.getByRole('checkbox', { name: /Restart the service/ }));
    await user.click(screen.getByRole('button', { name: 'Next' }));

    const expectedPayload = {
      projectId: 'proj-1',
      environment: 'Development',
      service: 'OrderProcessingService',
      machineId: 'machine-1',
      telemetryCredentialId: 'cred-1',
      expectedHostName: 'enrolled-host',
      kind: 'WindowsService',
      windowsServiceName: 'ScopedService',
      allowedOperations: ['RunHealthCheck', 'RestartService']
    };
    await waitFor(() => expect(remediationTargetsApi.preflight).toHaveBeenCalledWith({ ...expectedPayload, enabled: true }));
    expect(await screen.findByText('Windows service exists')).toBeInTheDocument();
    expect(screen.getByText(/All required checks pass/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Enable' }));
    await waitFor(() => expect(remediationTargetsApi.create).toHaveBeenCalledWith({ ...expectedPayload, enabled: true }));
  }, WIZARD_TIMEOUT);

  it('explains missing Windows rights with the exact fix command, keeps Enable off, and still allows saving disabled', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.preflight.mockResolvedValue(missingRightsPreflight);
    remediationTargetsApi.create.mockResolvedValue({ ...target, enabled: false });
    renderPage();
    await screen.findByText('No remediation targets yet');
    await fillWizardToOperations(user);
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('KAIRON needs permission to:')).toBeInTheDocument();
    expect(screen.getByText(/✓ start Scoped Orders Worker/)).toBeInTheDocument();
    expect(screen.getByText('✗ Administrator access')).toBeInTheDocument();
    expect(screen.getByText('✗ Access to unrelated services')).toBeInTheDocument();
    expect(screen.getByText('✗ Permission to modify service configuration')).toBeInTheDocument();
    expect(screen.getByText(/elevated PowerShell/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Copy fix command' }));
    expect(write).toHaveBeenCalledWith(missingRightsPreflight.fixCommand);

    remediationTargetsApi.preflight.mockClear();
    await user.click(screen.getByRole('button', { name: 'Re-check' }));
    await waitFor(() => expect(remediationTargetsApi.preflight).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByRole('button', { name: 'Enable' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Save disabled' }));
    await waitFor(() => expect(remediationTargetsApi.create).toHaveBeenCalledWith(expect.objectContaining({ enabled: false })));
    write.mockRestore();
  }, WIZARD_TIMEOUT);

  it('shows the field errors of a ProblemDetails 400 instead of a generic message', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.create.mockRejectedValue({
      status: 400,
      message: 'One or more validation errors occurred.',
      fieldErrors: ['MachineId: The MachineId field is required.']
    });
    renderPage();
    await screen.findByText('No remediation targets yet');
    await fillWizardToOperations(user);
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText(/All required checks pass/);
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Enable' }));

    expect(await screen.findByText('MachineId: The MachineId field is required.')).toBeInTheDocument();
    expect(screen.queryByText('One or more validation errors occurred.')).not.toBeInTheDocument();
  }, WIZARD_TIMEOUT);

  it('edits an existing target in the same wizard, prefilled, sending expectedUpdatedAt', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.update.mockResolvedValue({ ...target });
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    expect(screen.getByLabelText('Logical service')).toHaveValue('OrderProcessingService');
    expect(screen.getByLabelText('Environment')).toHaveValue('Production');
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('radio', { name: /Scoped Orders Worker/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByRole('checkbox', { name: /Restart the service/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText(/All required checks pass/);
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Save and enable' }));

    await waitFor(() => expect(remediationTargetsApi.update).toHaveBeenCalledWith('target-1', expect.objectContaining({
      windowsServiceName: 'ScopedService',
      allowedOperations: ['RestartService', 'RunHealthCheck'],
      enabled: true,
      expectedUpdatedAt: '2026-01-02T00:00:00Z'
    })));
  }, WIZARD_TIMEOUT);

  it('shows a clear message and does not overwrite on a stale-update conflict', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([target]);
    remediationTargetsApi.update.mockRejectedValue({ status: 409, code: 'stale-update', message: 'stale' });
    renderPage();
    await screen.findByText('Scoped Orders Worker');

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    for (let i = 0; i < 5; i += 1) {
      await user.click(screen.getByRole('button', { name: 'Next' }));
    }
    await user.click(screen.getByRole('button', { name: 'Save disabled' }));

    expect(await screen.findByText(
      'This target was changed by another operator. Reload the latest version before editing it again.'
    )).toBeInTheDocument();
  }, WIZARD_TIMEOUT);

  it('opens the wizard with the machine preselected via the machine-page shortcut', async () => {
    const user = userEvent.setup();
    agentApi.getMachines.mockResolvedValue([machine, { ...machine, id: 'machine-2', hostName: 'other-host' }]);
    remediationTargetsApi.list.mockResolvedValue([]);
    renderPage({ preselectMachineId: 'machine-1' });
    await user.selectOptions(await screen.findByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    await user.type(screen.getByLabelText('Logical service'), 'Svc');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByRole('radio', { name: /enrolled-host/ })).toBeChecked();
    expect(screen.getByRole('radio', { name: /other-host/ })).not.toBeChecked();
  }, WIZARD_TIMEOUT);
});
