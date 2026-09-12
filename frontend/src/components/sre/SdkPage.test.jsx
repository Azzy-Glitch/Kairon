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
  sessionStorage.clear();
  sdkApi.listProjects.mockResolvedValue([project]);
  sdkApi.listCredentials.mockResolvedValue([credential]);
});

describe('SdkPage re-pairing', () => {
  it('starts a re-pair, showing the fresh code and its expiration', async () => {
    renderSdkPage();
    await openPairingTab();

    await startRepair();

    expect(screen.getByText(/Re-pairing "orders-sdk"/)).toBeInTheDocument();
    expect(sdkApi.createPairing).toHaveBeenCalledWith('proj-1', 'dotnet', 'cred-1');
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

  it('disables starting a second re-pair while one is already in flight', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair('2099-01-01T00:10:00Z');

    expect(screen.getByRole('button', { name: /Re-pair/ })).toBeDisabled();
  });

  it('never stores the API key or any credential secret in sessionStorage - only safe recovery metadata', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair();

    const stored = sessionStorage.getItem('kairon:activeRepair');
    expect(stored).toBeTruthy();
    expect(stored).not.toContain('krn_');
    const parsed = JSON.parse(stored);
    expect(parsed).not.toHaveProperty('apiKey');
    expect(parsed).not.toHaveProperty('codeHash');
    expect(parsed.pairingId).toBe('pair-1');
    expect(parsed.projectId).toBe('proj-1');
  });

  it('recovers an in-flight re-pair after a page refresh and resumes polling from where it left off', async () => {
    const { unmount } = renderSdkPage();
    await openPairingTab();
    await startRepair();

    // Simulates a page refresh: the whole React tree unmounts and a fresh one mounts in its
    // place - sessionStorage (not component state) is all that survives across this boundary.
    unmount();

    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z' });
    sdkApi.completeRepair.mockResolvedValue({ rebindCount: 0 });

    renderSdkPage();
    await openPairingTab();

    expect(await screen.findByText(/Re-pairing "orders-sdk"/)).toBeInTheDocument();
    await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalledWith('pair-1', 'cred-1'), { timeout: POLL_WAIT_TIMEOUT });
  }, POLL_TEST_TIMEOUT);

  it('does not recover a session that already reached a terminal state before the refresh', async () => {
    const { unmount } = renderSdkPage();
    await openPairingTab();
    await startRepair();
    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Cancelled' });
    await waitFor(() => expect(sdkApi.getPairingStatus).toHaveBeenCalled(), { timeout: POLL_WAIT_TIMEOUT });
    await screen.findByText(/Cancelled - no credential was changed/i);

    unmount();
    renderSdkPage();
    await openPairingTab();

    expect(screen.queryByText(/Re-pairing "orders-sdk"/)).not.toBeInTheDocument();
  }, POLL_TEST_TIMEOUT);

  it('times out an awaiting-confirmation session that never gets confirmed, without polling forever', async () => {
    sessionStorage.setItem('kairon:activeRepair', JSON.stringify({
      projectId: 'proj-1', credentialId: 'cred-1', credentialName: 'orders-sdk',
      pairingId: 'pair-1', code: 'pair_freshcode', expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
      status: 'AwaitingConfirmation', confirmationDeadline: Date.now() - 1000
    }));

    renderSdkPage();
    await openPairingTab();

    expect(await screen.findByText(/never confirmed it received the new credential/i)).toBeInTheDocument();
    expect(sdkApi.getPairingStatus).not.toHaveBeenCalled();
  });

  it('does not pretend cancellation succeeded when the backend rejects it, and reflects the real status instead', async () => {
    renderSdkPage();
    await openPairingTab();
    await startRepair('2099-01-01T00:10:00Z');

    sdkApi.revokePairing.mockRejectedValue({ message: 'Pairing session not found.' });
    sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: null });

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(await screen.findByText('Pairing session not found.')).toBeInTheDocument();
    // Never silently cleared - the banner remains and now reflects the real, current status.
    expect(await screen.findByText(/waiting for the application to confirm/i)).toBeInTheDocument();
  });

  // --- Blocker 3: recovering the final completion stage after a refresh/lost response ---------

  describe('recovering a Completing session after refresh/lost response', () => {
    it('refreshing while Completing shows a reconnecting state, then resolves once the backend confirms completion', async () => {
      sessionStorage.setItem('kairon:activeRepair', JSON.stringify({
        projectId: 'proj-1', credentialId: 'cred-1', credentialName: 'orders-sdk',
        pairingId: 'pair-1', code: 'pair_freshcode', expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
        status: 'Completing'
      }));
      let resolveStatus;
      sdkApi.getPairingStatus.mockReturnValue(new Promise((resolve) => { resolveStatus = resolve; }));
      sdkApi.listCredentials.mockResolvedValue([{ ...credential, revokedAt: '2026-01-01T00:01:06Z' }]);

      renderSdkPage();
      await openPairingTab();

      // Never silently resumes as if the page had never reloaded - shows a distinct, honest
      // "checking" state instead of either "Completing..." or an immediate assumed outcome.
      expect(await screen.findByText(/checking whether the re-pair already finished/i, {}, { timeout: POLL_WAIT_TIMEOUT })).toBeInTheDocument();
      expect(sdkApi.completeRepair).not.toHaveBeenCalled();

      resolveStatus({ status: 'Completed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z', completedAt: '2026-01-01T00:01:06Z' });

      expect(await screen.findByText(/the old one has been revoked/i, {}, { timeout: POLL_WAIT_TIMEOUT })).toBeInTheDocument();
      expect(sdkApi.completeRepair).not.toHaveBeenCalled();
    }, POLL_TEST_TIMEOUT);

    it('detects a completion whose HTTP response was lost, without ever re-invoking the replacement operation', async () => {
      const { unmount } = renderSdkPage();
      await openPairingTab();
      await startRepair();

      // The application confirmed, so the operator's own tab starts completing the repair - but
      // its response never comes back (simulates the exact "server succeeded, response lost" case).
      sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z', completedAt: null });
      sdkApi.completeRepair.mockReturnValue(new Promise(() => {})); // never settles in this tab
      await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1), { timeout: POLL_WAIT_TIMEOUT });
      await screen.findByText(/completing the re-pair/i);

      // Simulates a refresh: the tree unmounts mid-flight, exactly when the response was lost.
      unmount();

      // The backend HAD actually finished it - only this tab's own view of the response was lost.
      sdkApi.getPairingStatus.mockResolvedValue({ status: 'Completed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z', completedAt: '2026-01-01T00:01:06Z' });
      sdkApi.listCredentials.mockResolvedValue([{ ...credential, revokedAt: '2026-01-01T00:01:06Z' }]);

      renderSdkPage();
      await openPairingTab();

      expect(await screen.findByText(/the old one has been revoked/i, {}, { timeout: POLL_WAIT_TIMEOUT })).toBeInTheDocument();
      // The recovery path only ever reads status - it must never call completeRepair a second time
      // just because the first call's response never arrived.
      expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);
    }, POLL_TEST_TIMEOUT);

    it('a genuine completion failure remains recoverable and retryable after a refresh', async () => {
      const { unmount } = renderSdkPage();
      await openPairingTab();
      await startRepair();

      sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z', completedAt: null });
      sdkApi.completeRepair.mockRejectedValue({ message: 'The new credential has not been confirmed by the application yet.' });
      await waitFor(() => expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1), { timeout: POLL_WAIT_TIMEOUT });
      await screen.findByText(/completing the re-pair failed/i);

      unmount();
      renderSdkPage();
      await openPairingTab();

      // The known failure and its Retry survive the refresh untouched - no backend recheck was
      // needed to know this one, unlike the lost-response case above.
      expect(await screen.findByText(/completing the re-pair failed/i)).toBeInTheDocument();
      expect(screen.getByText(/NOT revoked/i)).toBeInTheDocument();
      expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);

      sdkApi.completeRepair.mockResolvedValue({ rebindCount: 0 });
      sdkApi.listCredentials.mockResolvedValue([{ ...credential, revokedAt: '2026-01-01T00:02:00Z' }]);
      await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

      expect(await screen.findByText(/the old credential was revoked/i)).toBeInTheDocument();
      expect(sdkApi.completeRepair).toHaveBeenCalledTimes(2);
    }, POLL_TEST_TIMEOUT);

    it('never duplicates the replacement operation when retrying after the outcome could not be confirmed', async () => {
      sessionStorage.setItem('kairon:activeRepair', JSON.stringify({
        projectId: 'proj-1', credentialId: 'cred-1', credentialName: 'orders-sdk',
        pairingId: 'pair-1', code: 'pair_freshcode', expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
        status: 'Completing'
      }));
      // The backend never says either way within the bounded recovery window (still Confirmed,
      // never Completed) - a persistent uncertainty, not a quick "it just finished" case.
      sdkApi.getPairingStatus.mockResolvedValue({ status: 'Redeemed', redeemedAt: '2026-01-01T00:01:00Z', confirmedAt: '2026-01-01T00:01:05Z', completedAt: null });

      renderSdkPage();
      await openPairingTab();

      // Reaching CompletionUnknown takes COMPLETION_RECOVERY_MAX_ATTEMPTS checks, spaced by
      // REPAIR_POLL_INTERVAL_MS - a longer bound than the other assertions in this file.
      expect(await screen.findByText(/could not confirm whether this re-pair actually completed/i, {}, { timeout: REPAIR_POLL_INTERVAL_MS * 5 })).toBeInTheDocument();
      // Automatic recovery never calls the mutating endpoint on its own, no matter how long it polls.
      expect(sdkApi.completeRepair).not.toHaveBeenCalled();

      // The backend is idempotent-safe (see SdkPairingService.CompleteRepairAsync's own remarks) -
      // an explicit Retry here is safe even if the original attempt had actually already succeeded.
      sdkApi.completeRepair.mockResolvedValue({ rebindCount: 0 });
      sdkApi.listCredentials.mockResolvedValue([{ ...credential, revokedAt: '2026-01-01T00:02:00Z' }]);
      await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

      expect(await screen.findByText(/the old credential was revoked/i)).toBeInTheDocument();
      expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);

      // Idle afterward - a completed session must not keep calling completeRepair or polling status.
      const statusCallsAtCompletion = sdkApi.getPairingStatus.mock.calls.length;
      await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 2));
      expect(sdkApi.completeRepair).toHaveBeenCalledTimes(1);
      expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(statusCallsAtCompletion);
    }, REPAIR_POLL_INTERVAL_MS * 12);

    it('an expired session discovered during recovery stops polling and never retries', async () => {
      sessionStorage.setItem('kairon:activeRepair', JSON.stringify({
        projectId: 'proj-1', credentialId: 'cred-1', credentialName: 'orders-sdk',
        pairingId: 'pair-1', code: 'pair_freshcode', expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
        status: 'Completing'
      }));
      sdkApi.getPairingStatus.mockResolvedValue({ status: 'Expired' });

      renderSdkPage();
      await openPairingTab();

      expect(await screen.findByText(/This code expired before it was used/i, {}, { timeout: POLL_WAIT_TIMEOUT })).toBeInTheDocument();
      expect(sdkApi.completeRepair).not.toHaveBeenCalled();

      const callsAtExpiry = sdkApi.getPairingStatus.mock.calls.length;
      await new Promise((resolve) => setTimeout(resolve, REPAIR_POLL_INTERVAL_MS * 2));
      expect(sdkApi.getPairingStatus).toHaveBeenCalledTimes(callsAtExpiry);
    }, POLL_TEST_TIMEOUT);
  });
});
