import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ToastProvider } from '../Toast';
import SdkPage, { REPAIR_POLL_INTERVAL_MS } from './SdkPage';
import { sdkApi, healthApi, telemetryApi } from '../../api';

vi.mock('../../api', () => ({
  healthApi: { getHealth: vi.fn().mockResolvedValue('Healthy') },
  telemetryApi: { getTelemetryIncidents: vi.fn().mockResolvedValue([]), getMetrics: vi.fn().mockResolvedValue([]) },
  sdkApi: {
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listCredentials: vi.fn(),
    revokeCredential: vi.fn(),
    createPairing: vi.fn(),
    revokePairing: vi.fn(),
    getPairingStatus: vi.fn(),
    completeRepair: vi.fn()
  }
}));

// Polling tests wait for the real interval to fire (no fake timers - RTL's own findBy*/waitFor
// helpers poll using real timers internally and hang indefinitely once those are faked). A
// generous multiple of the real interval keeps this reliable without being slow.
const POLL_WAIT_TIMEOUT = REPAIR_POLL_INTERVAL_MS * 3;
const POLL_TEST_TIMEOUT = REPAIR_POLL_INTERVAL_MS * 6;

const project = { id: 'proj-1', name: 'Orders', activeCredentials: 1 };
// apiKey here is a decoy: the real listCredentials response never carries one (backend/DTOs -
// only id/name/keyPrefix/createdAt/revokedAt), but the UI must never render it even if it did.
const credential = { id: 'cred-1', name: 'orders-sdk', keyPrefix: 'krn_abcdef123456', createdAt: '2026-01-01T00:00:00Z', revokedAt: null, apiKey: 'krn_should_never_render_this_secret' };

function renderSdkPage() {
  return render(
    <ToastProvider>
      <SdkPage />
    </ToastProvider>
  );
}

async function openPairingTab() {
  await userEvent.click(screen.getByRole('tab', { name: 'Pairing' }));
  await screen.findByText('Orders');
  await screen.findByText('orders-sdk');
}

async function startRepair(expiresAt = new Date(Date.now() + 10 * 60 * 1000).toISOString()) {
  sdkApi.createPairing.mockResolvedValue({ pairingId: 'pair-1', code: 'pair_freshcode', expiresAt, sdkType: 'dotnet' });
  await userEvent.click(screen.getByRole('button', { name: /Re-pair/ }));
  await screen.findByText('pair_freshcode');
}

beforeEach(() => {
  vi.clearAllMocks();
  sdkApi.listProjects.mockResolvedValue([project]);
  sdkApi.listCredentials.mockResolvedValue([credential]);
});

describe('SdkPage re-pairing', () => {
  it('starts a re-pair, showing the fresh code and its expiration', async () => {
    renderSdkPage();
    await openPairingTab();

    await startRepair();

    expect(screen.getByText(/Re-pairing "orders-sdk"/)).toBeInTheDocument();
    expect(sdkApi.createPairing).toHaveBeenCalledWith('proj-1', 'dotnet');
  });

  it('never renders any credential secret, before or during a re-pair', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    expect(document.body.textContent).not.toContain('krn_should_never_render_this_secret');
  });

  it('shows an intermediate "awaiting confirmation" state on Redeemed, without completing the repair yet', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: null });

    await waitFor(() => expect(sdkApi.getPairingStatus).toHaveBeenCalled(), { timeout: POLL_WAIT_TIMEOUT });
    expect(await screen.findByText(/waiting for the application to confirm/i)).toBeInTheDocument();
    expect(sdkApi.completeRepair).not.toHaveBeenCalled();
  });

  it('completes the repair only once the application confirms the new credential, never on Redeemed alone', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z' });
    sdkApi.completeRepair.mockResolvedValue({ rebindCount: 2 });
    sdkApi.listCredentials.mockResolvedValue([{ ...credential, revokedAt: '2026-01-01T00:01:10Z' }]);

    await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalledWith('pair-1', 'cred-1'), { timeout: POLL_WAIT_TIMEOUT });
    expect(await screen.findByText(/the old credential was revoked/i)).toBeInTheDocument();
    expect(screen.getByText(/2 remediation targets now use it too/i)).toBeInTheDocument();

    // Polling must stop once a terminal state is reached - no further status calls after this.
    const callsAtCompletion = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 2));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(callsAtCompletion);
  }, POLL_TEST_TIMEOUT);

  it('reports completion failure clearly and never claims the old credential was revoked', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z' });
    sdkApi.completeRepair.mockRejectedValue({ message: 'The new credential has not been confirmed by the application yet.' });

    await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalled(), { timeout: POLL_WAIT_TIMEOUT });
    expect(await screen.findByText(/completing the re-pair failed/i)).toBeInTheDocument();
    expect(screen.getByText(/NOT revoked/i)).toBeInTheDocument();
    expect(sdkApi.revokeCredential).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  }, POLL_TEST_TIMEOUT);

  it('never calls completeRepair more than once even if multiple polling ticks observe confirmation', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    let resolveCompletion;
    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z' });
    sdkApi.completeRepair.mockReturnValue(new Promise((resolve) => { resolveCompletion = resolve; }));

    // Let several poll intervals elapse while completeRepair is still in flight - a second/third
    // tick observing the same confirmedAt must not trigger a second completion attempt.
    await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1), { timeout: POLL_WAIT_TIMEOUT });
    await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 3));
    expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);

    resolveCompletion({ rebindCount: 0 });
    await screen.findByText(/the old credential was revoked/i);
    expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);
  }, POLL_TEST_TIMEOUT);

  it('shows an expired message and stops polling once the code expires', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Expired' });

    expect(await screen.findByText(/This code expired before it was used/, {}, { timeout: POLL_WAIT_TIMEOUT })).toBeInTheDocument();
    expect(sdkApi.completeRepair).not.toHaveBeenCalled();

    const callsAtExpiry = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 2));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(callsAtExpiry);
  }, POLL_TEST_TIMEOUT);

  it('cancelling an in-flight re-pair revokes the pairing session and leaves the old credential untouched', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair('2099-01-01T00:10:00Z');
    sdkApi.revokePairing.mockResolvedValue({});

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(sdkApi.revokePairing).toHaveBeenCalledWith('pair-1'));
    expect(sdkApi.completeRepair).not.toHaveBeenCalled();
    expect(screen.queryByText('pair_freshcode')).not.toBeInTheDocument();
  });

  it('stops polling once the component unmounts', async () => {
    const { unmount } = renderSdkPage();
    await openPairingTab();
    await startRepair();
    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Pending' });

    await waitFor(() => expect(sdkApi.getPairingStatus).toHaveBeenCalled(), { timeout: POLL_WAIT_TIMEOUT });

    unmount();
    const callsAtUnmount = sdkApi.getPairingStatus.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 2));
    expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(callsAtUnmount);
  }, POLL_TEST_TIMEOUT);

  it('reports a failure to start re-pairing without crashing', async () => {
    renderSdkPage();
    await openPairingTab();
    sdkApi.createPairing.mockRejectedValue({ message: 'Could not start re-pairing' });

    await userEvent.click(screen.getByRole('button', { name: /Re-pair/ }));

    expect(await screen.findByText('Could not start re-pairing')).toBeInTheDocument();
  });
});
