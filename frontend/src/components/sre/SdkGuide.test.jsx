import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import SdkPage from './SdkPage';
import { healthApi, telemetryApi } from '../../api';

const { addToast } = vi.hoisted(() => ({ addToast: vi.fn() }));
vi.mock('../Toast', () => ({ useToast: () => ({ addToast }) }));
vi.mock('../../api', () => ({
  healthApi: { getHealth: vi.fn().mockResolvedValue('Healthy') },
  sdkApi: {
    listProjects: vi.fn().mockResolvedValue([{ id: 'project-1', name: 'Orders', activeCredentials: 1 }]),
    listCredentials: vi.fn().mockResolvedValue([])
  },
  telemetryApi: {
    getTelemetryIncidents: vi.fn().mockResolvedValue([]),
    getMetrics: vi.fn().mockResolvedValue([])
  }
}));

describe('SDK onboarding interactions', () => {
  it('opens live pairing without claiming active credentials prove connectivity', async () => {
    render(<SdkPage />);
    await userEvent.click(screen.getByRole('button', { name: 'Open Pairing' }));
    expect(await screen.findByText('1 active credential')).toBeInTheDocument();
    expect(screen.queryByText(/Connected ·/)).not.toBeInTheDocument();
  });

  it('shows the real .NET and Python integration examples with no extra click required', async () => {
    render(<SdkPage />);
    expect(await screen.findByText(/collector = Kairon\(pairing_code="YOUR_PAIRING_CODE"\)/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: /\.NET/ }));
    expect(await screen.findByText(/new KaironClient\(pairingCode: "YOUR_PAIRING_CODE"\)/)).toBeInTheDocument();
    expect(await screen.findByText(/builder\.Services\.AddKairon/)).toBeInTheDocument();
  });

  it('copies the pairing-code Python example, the new primary onboarding path', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(await screen.findByRole('button', { name: 'Copy python-fastapi-paired example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('Kairon(pairing_code="YOUR_PAIRING_CODE")'));
    write.mockRestore();
  });

  it('still offers the explicit-configuration Python example for CI/CD and containers', async () => {
    const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(screen.getByText('Using explicit configuration instead?'));
    await user.click(await screen.findByRole('button', { name: 'Copy python-fastapi-usage example' }));
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

it('shows real, polled telemetry status once a project has been paired, not merely because a credential exists', async () => {
  telemetryApi.getMetrics.mockResolvedValue([]);
  telemetryApi.getTelemetryIncidents.mockResolvedValue([
    { application: 'OrdersApp', service: 'OrdersService', environment: 'Development', timestamp: new Date().toISOString() }
  ]);
  render(<SdkPage />);

  // No project paired yet: the Verify step must not claim a connection.
  expect(await screen.findByText('Waiting for telemetry…')).toBeInTheDocument();

  await userEvent.click(screen.getByRole('button', { name: 'Open Pairing' }));
  await screen.findByText('1 active credential'); // Pairing auto-selected the only project.
  await userEvent.click(screen.getByRole('tab', { name: 'Get Started' }));

  expect(await screen.findByText('Connected', {}, { timeout: 3000 })).toBeInTheDocument();
  expect(screen.getByText('OrdersApp')).toBeInTheDocument();
  expect(screen.getByText('OrdersService')).toBeInTheDocument();
});
