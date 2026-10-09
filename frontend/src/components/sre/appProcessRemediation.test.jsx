import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ToastProvider } from '../Toast';
import RemediationTargetsPage from './RemediationTargetsPage';
import { AppRestartOffer } from './ConnectApp';
import { remediationTargetsApi, sdkApi, agentApi } from '../../api';
import { describeActionTarget } from '../../lib/operatorIdentity';
import { describeReadiness, classifyExecutionError, operationLabel } from '../../lib/remediation';

/**
 * Application-process remediation: KAIRON restarts the connected app itself (via the UserAgent, as
 * the same user) - no Windows service, no permission script. The wizard defaults to it and skips
 * the Windows-only steps; Connect an App offers it in one click.
 */
vi.mock('../../api', () => ({
  remediationTargetsApi: {
    list: vi.fn(), create: vi.fn(), update: vi.fn(), disable: vi.fn(), enable: vi.fn(), validate: vi.fn(),
    preflight: vi.fn(), targetPreflight: vi.fn(), machineServices: vi.fn(), enableAppRestartFromPairing: vi.fn()
  },
  sdkApi: { listProjects: vi.fn(), listCredentials: vi.fn(), listApplications: vi.fn() },
  agentApi: { getMachines: vi.fn() }
}));

const WIZARD_TIMEOUT = 20000;
const machine = { id: 'machine-1', hostName: 'enrolled-host', operatingSystem: 'Windows 11', status: 'Online' };

const appPreflight = {
  readiness: 'Ready',
  canEnable: true,
  checks: [
    { key: 'agent-proof', label: 'App telemetry is confirmed by the Agent on this machine', passed: true, blocking: false },
    { key: 'process', label: 'KAIRON knows which process is the application', passed: true, blocking: false,
      detail: 'C:\\Python314\\python.exe (process 4242) in D:\\apps\\checkout' },
    { key: 'useragent', label: "The KAIRON UserAgent in the app's user session can restart it", passed: true, blocking: false },
    { key: 'eligible-process', label: 'The process is an ordinary application (not Windows or KAIRON itself)', passed: true, blocking: true }
  ],
  service: null,
  requiredRights: [],
  missingRights: [],
  fixCommand: null
};

const appTarget = {
  id: 'target-app', projectId: 'proj-1', projectName: 'Orders', environment: 'Development', service: 'CheckoutDemo',
  machineId: 'machine-1', machineHostName: 'enrolled-host', telemetryCredentialId: 'cred-1', telemetryCredentialName: 'python-sdk',
  expectedHostName: 'enrolled-host', windowsServiceName: '', kind: 'AppProcess', processId: 4242,
  processExecutable: 'C:\\Python314\\python.exe', processWorkingDirectory: 'D:\\apps\\checkout',
  allowedOperations: ['RestartApplication'], enabled: true, readiness: 'Ready', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z'
};

beforeEach(() => {
  vi.clearAllMocks();
  sdkApi.listProjects.mockResolvedValue([{ id: 'proj-1', name: 'Orders' }]);
  sdkApi.listCredentials.mockResolvedValue([{ id: 'cred-1', name: 'python-sdk', keyPrefix: 'krn_abc', revokedAt: null }]);
  sdkApi.listApplications.mockResolvedValue([]);
  agentApi.getMachines.mockResolvedValue([machine]);
  remediationTargetsApi.preflight.mockResolvedValue(appPreflight);
  remediationTargetsApi.targetPreflight.mockResolvedValue(appPreflight);
});

function renderPage() {
  return render(<ToastProvider><RemediationTargetsPage /></ToastProvider>);
}

describe('Configure Target wizard - the application itself', () => {
  it('defaults to restarting the app, skips the Windows-only steps and sends an app-process target', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([]);
    remediationTargetsApi.create.mockResolvedValue(appTarget);
    renderPage();
    await screen.findByText('No remediation targets yet');
    await user.click(screen.getAllByRole('button', { name: '+ Configure Target' })[0]);

    expect(screen.getByRole('radio', { name: /The application itself/ })).toBeChecked();
    expect(screen.getByText(/Step 1 of 5/)).toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Project'), 'proj-1');
    await waitFor(() => expect(screen.getByLabelText('App credential')).toHaveValue('cred-1'));
    await user.type(screen.getByLabelText('Logical service'), 'CheckoutDemo');
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('radio', { name: /enrolled-host/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    // Straight to operations: no Windows service list is ever requested.
    expect(screen.getByRole('checkbox', { name: /Restart the application/ })).toBeChecked();
    expect(remediationTargetsApi.machineServices).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByRole('heading', { name: 'Readiness check' })).toBeInTheDocument();
    expect(await screen.findByText('KAIRON knows which process is the application')).toBeInTheDocument();
    expect(screen.queryByText(/elevated PowerShell/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Enable' }));

    await waitFor(() => expect(remediationTargetsApi.create).toHaveBeenCalledWith(expect.objectContaining({
      kind: 'AppProcess', windowsServiceName: '', allowedOperations: ['RestartApplication'], service: 'CheckoutDemo', enabled: true
    })));
  }, WIZARD_TIMEOUT);

  it('lists an app-process target as the app itself, with the program it would restart', async () => {
    remediationTargetsApi.list.mockResolvedValue([appTarget]);
    renderPage();
    expect(await screen.findByText('The app itself')).toBeInTheDocument();
    expect(screen.getByText('python.exe')).toBeInTheDocument();
  });

  it("shows the app's program and folder in the target view", async () => {
    const user = userEvent.setup();
    remediationTargetsApi.list.mockResolvedValue([appTarget]);
    renderPage();
    await screen.findByText('The app itself');
    await user.click(screen.getByRole('button', { name: 'View' }));
    expect(await screen.findByText('CheckoutDemo (the app itself)')).toBeInTheDocument();
    expect(screen.getByText('D:\\apps\\checkout')).toBeInTheDocument();
  });
});

describe('Connect an App - automatic recovery offer', () => {
  it('enables app restart in one click for the connected pairing', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.enableAppRestartFromPairing.mockResolvedValue(appTarget);
    render(<ToastProvider><AppRestartOffer pairingId="pair-1" /></ToastProvider>);

    await user.click(screen.getByRole('button', { name: 'Let KAIRON restart this app when I approve' }));

    expect(remediationTargetsApi.enableAppRestartFromPairing).toHaveBeenCalledWith('pair-1');
    expect(await screen.findByText(/Nothing runs until you approve it/)).toBeInTheDocument();
  });

  it('says plainly what is missing when the app is not confirmed yet', async () => {
    const user = userEvent.setup();
    remediationTargetsApi.enableAppRestartFromPairing.mockRejectedValue({
      message: 'The KAIRON Agent has not confirmed this application on this machine yet.'
    });
    render(<ToastProvider><AppRestartOffer pairingId="pair-1" /></ToastProvider>);

    await user.click(screen.getByRole('button', { name: 'Let KAIRON restart this app when I approve' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('has not confirmed this application');
  });

  it('shows the enabled state when the app already allows restarts', () => {
    render(<ToastProvider><AppRestartOffer pairingId="pair-1" enabledTargetId="target-app" /></ToastProvider>);
    expect(screen.getByText(/Nothing runs until you approve it/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Let KAIRON restart/ })).not.toBeInTheDocument();
  });
});

describe('plain-language vocabulary for app restarts', () => {
  it('describes what an approved app restart will act on', () => {
    expect(describeActionTarget({
      targetKind: 'AppProcess', targetHostName: 'enrolled-host',
      targetProcessExecutable: 'C:\\Python314\\python.exe', targetProcessWorkingDirectory: 'D:\\apps\\checkout'
    }, 'Development')).toBe(' (restarts python.exe in D:\\apps\\checkout, enrolled-host, Development)');
  });

  it('labels the new readiness states, operation and failures', () => {
    expect(describeReadiness('UserAgentOffline').label).toBe('User not signed in');
    expect(describeReadiness('ProcessUnknown').label).toBe('Waiting for the app');
    expect(operationLabel('RestartApplication')).toBe('Restart the application');
    expect(classifyExecutionError('UserAgentOffline: nothing changed').headline).toMatch(/not signed in/);
  });
});
