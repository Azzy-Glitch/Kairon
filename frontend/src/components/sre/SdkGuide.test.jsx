import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import SdkPage from './SdkPage';
import { healthApi } from '../../api';
const { addToast } = vi.hoisted(() => ({ addToast: vi.fn() }));
vi.mock('../Toast', () => ({ useToast: () => ({ addToast }) }));
vi.mock('../../api', () => ({ healthApi: { getHealth: vi.fn().mockResolvedValue('Healthy') }, sdkApi: {
  listProjects: vi.fn().mockResolvedValue([{ id: 'project', name: 'Orders', activeCredentials: 1 }]),
  listCredentials: vi.fn().mockResolvedValue([])
} }));
describe('SDK onboarding interactions', () => {
  it('opens live pairing without claiming active credentials prove connectivity', async () => {
    render(<SdkPage />);
    await userEvent.click(screen.getByRole('button', { name: 'Open Pairing' }));
    expect(await screen.findByText('1 active credential')).toBeInTheDocument();
    expect(screen.queryByText(/Connected ·/)).not.toBeInTheDocument();
  });
  it('copies the authenticated Python example', async () => {
    const user = userEvent.setup(); const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    render(<SdkPage />);
    await user.click(screen.getByText('Show Python integration example'));
    await user.click(screen.getByRole('button', { name: 'Copy python-usage example' }));
    expect(write).toHaveBeenCalledWith(expect.stringContaining('api_key=os.environ["KAIRON_API_KEY"]'));
    write.mockRestore();
  });
  it('reports clipboard failure instead of claiming success', async () => {
    addToast.mockClear(); const user = userEvent.setup();
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    render(<SdkPage />);
    await user.click(screen.getByRole('tab', { name: '.NET' }));
    await user.click(screen.getByText('Show .NET integration example'));
    await user.click(screen.getByRole('button', { name: 'Copy dotnet-usage example' }));
    await waitFor(() => expect(addToast).toHaveBeenCalledWith('Copy failed. Select the text and copy it manually.', 'error'));
    expect(addToast).not.toHaveBeenCalledWith('Copied to clipboard', 'info'); write.mockRestore();
  });
});


it('checks the existing backend health endpoint and reports failure honestly', async () => {
  render(<SdkPage />);
  await userEvent.click(screen.getByRole('button', { name: 'Check Connection' }));
  expect(await screen.findByRole('status')).toHaveTextContent('backend is reachable');
  healthApi.getHealth.mockRejectedValueOnce(new Error('offline'));
  await userEvent.click(screen.getByRole('button', { name: 'Check Connection' }));
  expect(await screen.findByRole('status')).toHaveTextContent('Cannot reach KAIRON');
});

it('opens telemetry through the supplied navigation and keeps advanced reference collapsed', async () => {
  const navigate = vi.fn();
  render(<SdkPage onTelemetry={navigate} />);
  expect(screen.getByText('Advanced / Troubleshooting').closest('details')).not.toHaveAttribute('open');
  await userEvent.click(screen.getByRole('button', { name: 'View Live Telemetry' }));
  expect(navigate).toHaveBeenCalledOnce();
  await userEvent.click(screen.getByText('Advanced / Troubleshooting'));
  expect(screen.getByText('Advanced / Troubleshooting').closest('details')).toHaveAttribute('open');
  expect(screen.getByRole('heading', { name: 'Troubleshooting' })).toBeInTheDocument();
});
