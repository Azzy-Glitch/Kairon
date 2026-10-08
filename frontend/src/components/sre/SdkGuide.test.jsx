import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import SdkPage from './SdkPage';
import { healthApi, sdkApi } from '../../api';
import { CONNECTION_POLL_INTERVAL_MS } from './ConnectApp';

const { addToast } = vi.hoisted(() => ({ addToast: vi.fn() }));
vi.mock('../Toast', () => ({ useToast: () => ({ addToast }) }));
vi.mock('../../api', () => ({
  healthApi: { getHealth: vi.fn().mockResolvedValue('Healthy') },
  sdkApi: {
    listProjects: vi.fn().mockResolvedValue([{ id: 'project-1', name: 'Orders', activeCredentials: 1 }]),
    listCredentials: vi.fn().mockResolvedValue([]),
    listApplications: vi.fn().mockResolvedValue([{ id: 'app-1', service: 'OrdersService', lastTelemetryAt: null }]),
    createPairing: vi.fn(),
    getPairingStatus: vi.fn()
  },
  telemetryApi: {
    getTelemetryIncidents: vi.fn().mockResolvedValue([]),
    getMetrics: vi.fn().mockResolvedValue([])
  }
}));

describe('SDK onboarding interactions', () => {
  // Explicit, generous timeout (vitest's default is 5000ms): directly observed timing out on this
  // shared, actively-used development machine (Word, VS Code, and a live-wallpaper process were
  // all running at the same time) despite this test's own real work being short - confirming the
  // constraint is this host's current real, ongoing load, not this test's length or logic. See
  // RemediationTargetsPage.test.jsx's equivalent note for the fuller investigation.
  it('opens on the four-step Connect flow, with credentials and internals under Advanced / How it works', async () => {
    render(<SdkPage />);
    expect(screen.getByRole('heading', { name: 'How do I connect my application to KAIRON?' })).toBeInTheDocument();
    for (const title of ['Select your framework', 'Generate a pairing code', 'Add KAIRON to your application', 'Run your application']) {
      expect(screen.getByRole('heading', { name: title })).toBeInTheDocument();
    }
    expect(screen.getByText('Advanced / How it works').closest('details')).not.toHaveAttribute('open');
    // Python FastAPI is the default framework, and the button says which SDK the code is for.
    expect(screen.getByRole('radio', { name: 'FastAPI' })).toBeChecked();
    expect(await screen.findByRole('button', { name: 'Generate Python pairing code' })).toBeEnabled();
    expect(screen.queryByText(/Connected ·/)).not.toBeInTheDocument();
  }, 15000);

  it('shows the real .NET and Python integration examples with no extra click required', async () => {
    render(<SdkPage />);
    expect((await screen.findAllByText(/Kairon\.attach\(app, pairing_code="YOUR_PAIRING_CODE"\)/)).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('tab', { name: /\.NET/ }));
    expect(await screen.findByText(/AddKaironAsync\(pairingCode\)/)).toBeInTheDocument();
    expect((await screen.findAllByText(/builder\.Services\.AddKairon\(\)/)).length).toBeGreaterThan(0);
  });

  it('copies the pairing-code Python example, the new primary onboarding path', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(await screen.findByRole('button', { name: 'Copy python-fastapi-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon.attach(app, pairing_code="YOUR_PAIRING_CODE")'));
    expect(write).toHaveBeenCalledWith(expect.not.stringContaining('add_middleware'));
    expect(write).toHaveBeenCalledWith(expect.not.stringContaining('.start()'));
    write.mockRestore();
  });

  it('shows tested Flask, Django and generic protocol integrations without pretending Django uses FastAPI', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(screen.getByText('Flask, Django, or another ASGI/WSGI application'));
    await user.click(await screen.findByRole('button', { name: 'Copy python-flask-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon.attach(app, pairing_code='));
    await user.click(screen.getByRole('button', { name: 'Copy python-django-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('kairon.django.KaironMiddleware'));
    await user.click(screen.getByRole('button', { name: 'Copy python-asgi-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon.wrap_asgi'));
    await user.click(screen.getByRole('button', { name: 'Copy python-wsgi-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon.wrap_wsgi'));
    write.mockRestore();
  });

  it('still offers the explicit-configuration Python example for CI/CD and containers', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(screen.getByText('Advanced'));
    await user.click(await screen.findByRole('button', { name: 'Copy python-fastapi-manual example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('api_key=os.environ["KAIRON_API_KEY"]'));
    write.mockRestore();
  });

  it('reports clipboard failure instead of claiming success', async () => {
    addToast.mockClear();
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    render(<SdkPage />);
    await user.click(screen.getByRole('tab', { name: /\.NET/ }));
    await user.click(await screen.findByRole('button', { name: 'Copy dotnet-usage example' }));
    await waitFor(() => expect(addToast).toHaveBeenCalledWith('Copy failed. Select the text and copy it manually.', 'error'));
    expect(addToast).not.toHaveBeenCalledWith('Copied to clipboard', 'info');
    write.mockRestore();
  });

  it('marks Python and .NET as real, keyboard-accessible tabs', async () => {
    render(<SdkPage />);
    const python = screen.getByRole('tab', { name: /Python/ });
    const dotnet = screen.getByRole('tab', { name: /\.NET/ });
    expect(python).toHaveAttribute('aria-selected', 'true');
    expect(dotnet).toHaveAttribute('aria-selected', 'false');
    await userEvent.click(dotnet);
    expect(dotnet).toHaveAttribute('aria-selected', 'true');
    expect(python).toHaveAttribute('aria-selected', 'false');
  });
});

it('checks backend reachability automatically and reports failure honestly on re-check', async () => {
  render(<SdkPage />);
  expect(await screen.findByText('KAIRON is running')).toBeInTheDocument();
  healthApi.getHealth.mockRejectedValueOnce(new Error('offline'));
  await userEvent.click(screen.getByRole('button', { name: 'Check Connection' }));
  expect(await screen.findByText(/not reachable/)).toBeInTheDocument();
});

it('opens telemetry through the supplied navigation before any project is paired', async () => {
  const navigate = vi.fn();
  render(<SdkPage onTelemetry={navigate} />);
  await userEvent.click(await screen.findByRole('button', { name: 'View Live Telemetry' }));
  expect(navigate).toHaveBeenCalledOnce();
});

it('opens remediation through the supplied navigation', async () => {
  const navigate = vi.fn();
  render(<SdkPage onRemediation={navigate} />);
  await userEvent.click(screen.getByRole('button', { name: 'Configure remediation →' }));
  expect(navigate).toHaveBeenCalledOnce();
});

it('states the remote/cloud KAIRON_ENDPOINT requirement in the primary pairing step, not only under Advanced', async () => {
  render(<SdkPage />);
  // The main "Pair your application" step must carry this, not merely a buried Advanced section -
  // otherwise a remote/cloud KAIRON pairing attempt tries to reach 127.0.0.1:8000 with no signal
  // pointing at why.
  expect(await screen.findByText(/Remote or cloud KAIRON/)).toBeInTheDocument();
  expect(screen.getAllByText(/KAIRON_ENDPOINT/).length).toBeGreaterThan(0);
});

it('documents first-run ASP.NET pairing and automatic stored-credential reuse', async () => {
  const user = userEvent.setup();
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
  render(<SdkPage />);
  await user.click(screen.getByRole('tab', { name: /\.NET/ }));
  const useKairon = (await screen.findAllByText('UseKairon()')).at(-1);
  expect(useKairon.closest('p')).toHaveTextContent(/remains explicit/);
  await user.click(screen.getByRole('button', { name: 'Copy dotnet-usage example' }));
  expect(write).toHaveBeenCalledWith(expect.stringContaining('AddKaironAsync'));
  expect(write).toHaveBeenCalledWith(expect.stringContaining('builder.Services.AddKairon()'));
  expect(write).toHaveBeenCalledWith(expect.stringContaining('app.UseKairon()'));
  write.mockRestore();
});

it('keeps Troubleshooting and Advanced collapsed until opened, each with real content', async () => {
  render(<SdkPage />);
  const troubleshooting = screen.getByText('Troubleshooting').closest('details');
  const advanced = screen.getByText('Advanced').closest('details');
  expect(troubleshooting).not.toHaveAttribute('open');
  expect(advanced).not.toHaveAttribute('open');

  await userEvent.click(screen.getByText('Troubleshooting'));
  expect(troubleshooting).toHaveAttribute('open');
  expect(screen.getByRole('heading', { name: /KAIRON isn't reachable/ })).toBeInTheDocument();

  await userEvent.click(screen.getByText('Advanced'));
  expect(advanced).toHaveAttribute('open');
  expect(screen.getByRole('heading', { name: 'Networking' })).toBeInTheDocument();
});

it('never suggests a plain-HTTP remote endpoint, which the SDK refuses', async () => {
  render(<SdkPage />);
  await screen.findByRole('option', { name: 'Orders' });
  expect(document.body.textContent).not.toMatch(/http:\/\/your-kairon/);
  expect(screen.getAllByText(/HTTPS/).length).toBeGreaterThan(0);
});

describe('Connect flow: one framework choice drives the pairing code and the code shown', () => {
  const pending = { pairingId: 'pairing-1', status: 'Pending', redeemedAt: null, confirmedAt: null, connection: null };

  beforeEach(() => {
    sdkApi.createPairing.mockReset();
    sdkApi.getPairingStatus.mockReset();
    sdkApi.createPairing.mockImplementation((projectId, sdkType, _replaces, defaults) => Promise.resolve({
      pairingId: 'pairing-1',
      code: 'pair_' + sdkType + '_code',
      expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
      sdkType,
      environment: defaults?.environment,
      service: defaults?.service || null
    }));
    sdkApi.getPairingStatus.mockResolvedValue(pending);
  });

  it('generates a Python code for a Python framework and inserts it into that framework minimal code', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await screen.findByRole('option', { name: 'Orders' });
    await user.click(screen.getByRole('radio', { name: 'Flask' }));
    await user.type(screen.getByLabelText('Service name (optional)'), 'OrdersService');
    await user.click(screen.getByRole('button', { name: 'Generate Python pairing code' }));

    await waitFor(() => expect(sdkApi.createPairing).toHaveBeenCalledWith(
      'project-1', 'python', undefined, { environment: 'Development', service: 'OrdersService' }
    ));
    await user.click(await screen.findByRole('button', { name: 'Copy connect-flask example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon.attach(app, pairing_code="pair_python_code")'));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('from flask import Flask'));
    await user.click(screen.getByRole('button', { name: 'Copy install-flask example' }));
    expect(write).toHaveBeenCalledWith('pip install kairon-sdk');
    write.mockRestore();
  }, 15000);

  it('generates a .NET code for ASP.NET Core, and never shows a code in a snippet whose SDK cannot redeem it', async () => {
    const user = userEvent.setup();
    render(<SdkPage />);
    await screen.findByRole('option', { name: 'Orders' });
    await user.click(screen.getByRole('radio', { name: 'ASP.NET Core' }));
    await user.selectOptions(screen.getByLabelText('Environment'), 'Staging');
    await user.click(screen.getByRole('button', { name: 'Generate .NET pairing code' }));

    await waitFor(() => expect(sdkApi.createPairing).toHaveBeenCalledWith(
      'project-1', 'dotnet', undefined, { environment: 'Staging', service: '' }
    ));
    expect(await screen.findByText(/await builder\.Services\.AddKaironAsync\("pair_dotnet_code"\);/)).toBeInTheDocument();

    // Switching to a Python framework afterwards must not put the .NET code into Python code.
    await user.click(screen.getByRole('radio', { name: 'FastAPI' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/generated for \.NET/);
    expect(screen.queryByText(/pairing_code="pair_dotnet_code"/)).not.toBeInTheDocument();
  }, 15000);

  it('detects the connection from the pairing session: waiting, then connected, machine and telemetry - never a secret', async () => {
    const user = userEvent.setup();
    sdkApi.getPairingStatus
      .mockResolvedValueOnce(pending)
      .mockResolvedValue({
        pairingId: 'pairing-1',
        status: 'Confirmed',
        redeemedAt: '2026-01-01T00:00:01Z',
        confirmedAt: '2026-01-01T00:00:02Z',
        apiKey: 'krn_should_never_render_this_secret',
        connection: {
          lastTelemetryAt: new Date().toISOString(),
          application: 'OrdersApp',
          service: 'OrdersService',
          environment: 'Development',
          source: 'python-sdk',
          machineHostName: 'build-host-01',
          machineConfirmedAt: '2026-01-01T00:00:03Z'
        }
      });
    render(<SdkPage />);
    await screen.findByRole('option', { name: 'Orders' });
    await user.click(screen.getByRole('button', { name: 'Generate Python pairing code' }));

    expect(await screen.findByText('Waiting for application…')).toBeInTheDocument();
    expect(await screen.findByText('Your application is connected', {}, { timeout: CONNECTION_POLL_INTERVAL_MS * 3 })).toBeInTheDocument();
    expect(screen.getByText('build-host-01')).toBeInTheDocument();
    expect(screen.getByText('OrdersApp')).toBeInTheDocument();
    expect(screen.getByText('python-sdk')).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('krn_should_never_render_this_secret');

    // Fully connected: polling stops.
    const calls = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, CONNECTION_POLL_INTERVAL_MS + 500));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(calls);
  }, 20000);

  it('stops polling and says so when the pairing code expires', async () => {
    const user = userEvent.setup();
    sdkApi.getPairingStatus.mockResolvedValue({ ...pending, status: 'Expired' });
    render(<SdkPage />);
    await screen.findByRole('option', { name: 'Orders' });
    await user.click(screen.getByRole('button', { name: 'Generate Python pairing code' }));

    expect(await screen.findByText('This pairing code expired before it was used', {}, { timeout: CONNECTION_POLL_INTERVAL_MS * 3 })).toBeInTheDocument();
    const calls = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, CONNECTION_POLL_INTERVAL_MS + 500));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(calls);
  }, 20000);

  it('stops polling when the page unmounts', async () => {
    const user = userEvent.setup();
    const { unmount } = render(<SdkPage />);
    await screen.findByRole('option', { name: 'Orders' });
    await user.click(screen.getByRole('button', { name: 'Generate Python pairing code' }));
    await screen.findByText('Waiting for application…');
    unmount();
    const calls = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, CONNECTION_POLL_INTERVAL_MS + 500));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(calls);
  }, 15000);
});
