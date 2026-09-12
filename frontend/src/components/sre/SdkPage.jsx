import React, { useEffect, useRef, useState } from 'react';
import { sdkApi } from '../../api';
import { useToast } from '../Toast';
import Tabs from '../ui/Tabs';
import SdkGuide from './SdkGuide';
import { IconLink, IconCopy, IconCheck, IconTrash, IconRefresh, IconAlertTriangle } from '../Icons';

// Bounded polling for an outstanding pairing/re-pair session: stops on redemption, expiration,
// cancellation, or unmount - never runs forever. 3s keeps the UI responsive to a fast redemption
// (a developer running the SDK right after copying the code) without hammering the backend.
export const REPAIR_POLL_INTERVAL_MS = 3000;

// A redeemed-but-never-confirmed session must not poll forever if the application that redeemed
// it never actually confirms (crashed, was uninstalled, network permanently blocked) - this is a
// separate, much longer bound than the 10-minute pairing CODE expiry, which no longer applies
// once the code has already been redeemed.
export const AWAITING_CONFIRMATION_TIMEOUT_MS = 30 * 60 * 1000;

// Bounds RecoveringCompletion (see its own remarks below): how many read-only status checks to
// make - immediate, then spaced by REPAIR_POLL_INTERVAL_MS - before giving up and surfacing a
// safe, explicit Retry rather than checking forever.
export const COMPLETION_RECOVERY_MAX_ATTEMPTS = 3;

// Session-only (cleared when the tab closes), never localStorage: recovery state must survive a
// refresh/navigation within the same tab, but this is exactly as sensitive as anything else already
// visible in this UI while the tab is open - never more. Only non-secret pairing-session metadata is
// ever stored here - no API key, no pairing-code hash, nothing the backend itself would not already
// return from a plain status poll.
const REPAIR_STORAGE_KEY = 'kairon:activeRepair';

function loadStoredRepair() {
  try {
    const raw = sessionStorage.getItem(REPAIR_STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function saveStoredRepair(projectId, repair) {
  try {
    if (!repair || NON_RECOVERABLE_REPAIR_STATUSES.has(repair.status)) {
      sessionStorage.removeItem(REPAIR_STORAGE_KEY);
      return;
    }
    sessionStorage.setItem(REPAIR_STORAGE_KEY, JSON.stringify({ projectId, ...repair }));
  } catch {
    // Best-effort: a full/blocked sessionStorage must never break the re-pair flow itself.
  }
}

const TERMINAL_REPAIR_STATUSES = new Set(['Completed', 'CompletionFailed', 'CompletionUnknown', 'Expired', 'Cancelled', 'ConfirmationTimedOut']);
// Unlike TERMINAL_REPAIR_STATUSES (which just means "not actively polling, safe to start another
// re-pair"), these are the statuses truly worth forgetting entirely on refresh: nothing further can
// ever happen from here (Completed has nothing left to do; Expired/Cancelled/ConfirmationTimedOut
// never touched anything). CompletionFailed/CompletionUnknown are deliberately EXCLUDED - both
// still have a safe, meaningful Retry (completeRepairOnce is idempotent server-side), so losing
// that banner to an accidental refresh would strand the operator with no way back to it short of
// starting an entirely new pairing session.
const NON_RECOVERABLE_REPAIR_STATUSES = new Set(['Completed', 'Expired', 'Cancelled', 'ConfirmationTimedOut']);
const POLLING_REPAIR_STATUSES = new Set(['Pending', 'AwaitingConfirmation']);
// A stored session is only ever worth rehydrating across a refresh if something could still
// legitimately be done about it - see NON_RECOVERABLE_REPAIR_STATUSES above for what's excluded.
// 'Completing'/'RecoveringCompletion' are included here (unlike POLLING_REPAIR_STATUSES, which
// only drives the "waiting for redemption/confirmation" poll loop) because losing them on
// rehydration is exactly the recoverability gap this guards against: the completion request may
// have already reached the server and succeeded before the refresh, so resuming must always go
// through a fresh, read-only status check (RecoveringCompletion below), never straight back into
// 'Completing' as if the page had never reloaded. 'CompletionFailed'/'CompletionUnknown' rehydrate
// as themselves (no recheck needed - both are already a settled, known local outcome with a
// meaningful Retry) rather than being routed through RecoveringCompletion.
const RECOVERABLE_REPAIR_STATUSES = new Set([
  'Pending', 'AwaitingConfirmation', 'Completing', 'RecoveringCompletion', 'CompletionFailed', 'CompletionUnknown'
]);

const VIEWS = [
  { id: 'start', label: 'Get Started' },
  { id: 'pairing', label: 'Pairing' }
];

// SDK guide content is checked against the shipped SDK APIs. Pairing remains live.
export default function SdkPage({ onTelemetry, onRemediation }) {
  const [view, setView] = useState('start');
  // Lets the Get Started guide's Verify step poll real telemetry for the project the operator is
  // actually working with in Pairing, without inventing a new API - it just reuses whichever
  // project was last selected there.
  const [pairedProjectId, setPairedProjectId] = useState(null);

  return (
    <div className="animate-fade-in">
      <Tabs items={VIEWS} activeId={view} onChange={setView} className="dev-tools-subnav" />

      {view === 'start' && (
        <SdkGuide
          CodeBlock={CodeBlock}
          onPairing={() => setView('pairing')}
          onTelemetry={onTelemetry}
          onRemediation={onRemediation}
          projectId={pairedProjectId}
        />
      )}
      {view === 'pairing' && <Pairing onProjectSelected={setPairedProjectId} />}
    </div>
  );
}

function useCopy() {
  const toast = useToast();
  const [copiedKey, setCopiedKey] = useState(null);

  const copy = async (text, key) => {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      toast.addToast("Copy failed. Select the text and copy it manually.", "error");
      return;
    }
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
    toast.addToast('Copied to clipboard', 'info');
  };

  return { copiedKey, copy };
}

function CodeBlock({ code, copyKey }) {
  const { copiedKey, copy } = useCopy();
  return (
    <div className="sdk-code-block">
      <button type="button" className="small-btn sdk-code-copy" aria-label={`Copy ${copyKey} example`} onClick={() => copy(code, copyKey)}>
        {copiedKey === copyKey ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
      </button>
      <pre><code>{code}</code></pre>
    </div>
  );
}

function Pairing({ onProjectSelected }) {
  const toast = useToast();
  const [projects, setProjects] = useState(null);
  const [selectedId, setSelectedId] = useState(null);

  useEffect(() => { onProjectSelected?.(selectedId); }, [selectedId]); // eslint-disable-line react-hooks/exhaustive-deps
  const [newName, setNewName] = useState('');
  const [creating, setCreating] = useState(false);
  const [credentials, setCredentials] = useState(null);
  const [sdkType, setSdkType] = useState('dotnet');
  const [pairing, setPairing] = useState(false);
  const [pairingResult, setPairingResult] = useState(null);
  // A re-pair is just another pairing session, scoped to replacing one specific existing
  // credential once the fresh code is redeemed AND confirmed - see the polling effect and
  // completeRepairOnce below. Status values: Pending -> AwaitingConfirmation (redeemed, but the
  // application has not yet proven it received/persisted the new credential) -> Completing ->
  // Completed | CompletionFailed, or Expired | Cancelled at any point before those. A refresh/
  // navigation/lost-response while Completing rehydrates as RecoveringCompletion instead (never
  // straight back into Completing - see the rehydration effect and the RecoveringCompletion
  // effect below), which itself resolves to Completed | CompletionUnknown (genuinely could not
  // confirm either way - distinct from CompletionFailed, which means the backend actually said no)
  // | Expired | Cancelled.
  const [repair, setRepair] = useState(null); // { credentialId, credentialName, pairingId, code, expiresAt, status, confirmationDeadline?, rebindCount?, completionError?, recoveryAttempts? }
  const { copiedKey, copy } = useCopy();
  // Guards against a re-pair completion running more than once: JavaScript callbacks already
  // queued by a prior interval tick can still execute even after clearInterval, so relying on
  // clearInterval alone is not enough to make "observed Confirmed -> complete the repair" happen
  // exactly once (audit Phase 7).
  const completionInFlightRef = useRef(new Set());
  const rehydratedRef = useRef(false);

  // Recovers an in-flight re-pair across a page refresh/navigation within the same tab (runs once,
  // as soon as the project it belongs to is known - never re-redeems or re-generates anything,
  // only resumes polling an already-existing session), AND keeps the recovery snapshot in sync
  // with the actual UI state afterward - cleared the moment a terminal state is reached (see
  // saveStoredRepair), so a later refresh never resurrects a finished/cancelled/expired session.
  // Combined into one effect deliberately: rehydrating via setRepair() cannot also synchronously
  // save in that SAME pass (repair here would still be the stale pre-rehydration value, null,
  // which would immediately overwrite/erase the very state just read back) - returning early after
  // rehydrating lets the next render (once setRepair's update actually applies) do the save.
  useEffect(() => {
    if (!selectedId) return;
    if (!rehydratedRef.current) {
      rehydratedRef.current = true;
      const stored = loadStoredRepair();
      if (stored && stored.projectId === selectedId && RECOVERABLE_REPAIR_STATUSES.has(stored.status)) {
        const { projectId: _projectId, ...rest } = stored;
        // A session that was 'Completing' (or already recovering) before this page loaded must
        // never resume as if nothing happened - the in-flight completeRepair call from before may
        // have already reached the server and succeeded, with only its HTTP response lost. Recover
        // via a fresh, read-only status check (see the RecoveringCompletion effect below) instead
        // of silently trusting the stale local label or blindly re-invoking completeRepair.
        const needsRecoveryCheck = rest.status === 'Completing' || rest.status === 'RecoveringCompletion';
        setRepair(needsRecoveryCheck ? { ...rest, status: 'RecoveringCompletion', recoveryAttempts: 0 } : rest);
        return;
      }
    }
    saveStoredRepair(selectedId, repair);
  }, [selectedId, repair]);

  const refreshCredentials = () => {
    if (!selectedId) return;
    sdkApi.listCredentials(selectedId).then(setCredentials).catch(() => {});
  };

  const completeRepairOnce = async (current) => {
    if (completionInFlightRef.current.has(current.pairingId)) return;
    completionInFlightRef.current.add(current.pairingId);
    setRepair((prev) => (prev && prev.pairingId === current.pairingId ? { ...prev, status: 'Completing' } : prev));
    try {
      const result = await sdkApi.completeRepair(current.pairingId, current.credentialId);
      setRepair((prev) => (prev && prev.pairingId === current.pairingId
        ? { ...prev, status: 'Completed', rebindCount: result.rebindCount }
        : prev));
      refreshCredentials();
      const rebindNote = result.rebindCount > 0
        ? ` ${result.rebindCount} remediation target${result.rebindCount === 1 ? '' : 's'} now use it too.`
        : '';
      toast.addToast(`Re-paired "${current.credentialName}" - the old credential was revoked.${rebindNote}`, 'success');
    } catch (err) {
      // Never claim the old credential was revoked when this call did not actually succeed - the
      // backend only revokes it as part of this same request completing successfully.
      setRepair((prev) => (prev && prev.pairingId === current.pairingId
        ? { ...prev, status: 'CompletionFailed', completionError: err?.message }
        : prev));
      toast.addToast(
        err?.message || 'The new credential is active, but completing the re-pair failed - the old credential was left untouched. Try again.',
        'error'
      );
    } finally {
      completionInFlightRef.current.delete(current.pairingId);
    }
  };

  useEffect(() => {
    if (!repair || !POLLING_REPAIR_STATUSES.has(repair.status)) return undefined;
    if (repair.status === 'Pending' && Date.now() >= new Date(repair.expiresAt).getTime()) {
      setRepair((prev) => (prev ? { ...prev, status: 'Expired' } : prev));
      return undefined;
    }
    // A session that is Redeemed but never Confirmed must not poll forever if the application
    // that redeemed it never actually confirms - a separate, much longer bound than the pairing
    // code's own 10-minute expiry, which stopped applying the moment it was redeemed.
    if (repair.status === 'AwaitingConfirmation' && repair.confirmationDeadline
      && Date.now() >= repair.confirmationDeadline) {
      setRepair((prev) => (prev ? { ...prev, status: 'ConfirmationTimedOut' } : prev));
      return undefined;
    }

    let cancelled = false;
    const id = setInterval(async () => {
      if (cancelled) return;
      try {
        const status = await sdkApi.getPairingStatus(repair.pairingId);
        if (cancelled) return;
        if (status.status === 'Expired' || status.status === 'Cancelled') {
          setRepair((prev) => (prev && prev.pairingId === repair.pairingId ? { ...prev, status: status.status } : prev));
        } else if (status.confirmedAt) {
          // Redeemed proves the backend issued a credential; confirmedAt proves the application
          // itself received and is using it. Only confirmedAt makes it safe to complete the
          // repair (which revokes the credential being replaced) - see completeRepairOnce.
          await completeRepairOnce(repair);
        } else if (status.redeemedAt) {
          setRepair((prev) => (prev && prev.pairingId === repair.pairingId && prev.status === 'Pending'
            ? { ...prev, status: 'AwaitingConfirmation', confirmationDeadline: Date.now() + AWAITING_CONFIRMATION_TIMEOUT_MS }
            : prev));
        } else if (repair.status === 'Pending' && Date.now() >= new Date(repair.expiresAt).getTime()) {
          setRepair((prev) => (prev ? { ...prev, status: 'Expired' } : prev));
        } else if (repair.status === 'AwaitingConfirmation' && repair.confirmationDeadline && Date.now() >= repair.confirmationDeadline) {
          setRepair((prev) => (prev ? { ...prev, status: 'ConfirmationTimedOut' } : prev));
        }
      } catch {
        // A transient poll failure is not a terminal state - keep polling until the bound above.
      }
    }, REPAIR_POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [repair?.pairingId, repair?.status, repair?.confirmationDeadline]);

  // Recovers a re-pair completion whose outcome is unknown after a refresh/navigation/lost
  // response (rehydrated above as 'RecoveringCompletion', never straight back into 'Completing').
  // Only ever calls the read-only status endpoint here - never completeRepair - so a completion
  // that already succeeded before the refresh is discovered, not repeated, and one that never
  // actually went through is never silently assumed to have happened either. Bounded: after
  // COMPLETION_RECOVERY_MAX_ATTEMPTS checks with no definite answer, surfaces a safe, explicit
  // Retry (completeRepairOnce is itself idempotent server-side - see its own remarks) rather than
  // checking forever.
  useEffect(() => {
    if (!repair || repair.status !== 'RecoveringCompletion') return undefined;
    let cancelled = false;

    const check = async () => {
      if (cancelled) return;
      try {
        const status = await sdkApi.getPairingStatus(repair.pairingId);
        if (cancelled) return;
        if (status.completedAt) {
          // Authoritative: the backend's own record of THIS session's completion, set only once
          // (SdkPairingService.CompleteRepairAsync) and never any other way - safe to trust without
          // re-running anything.
          setRepair((prev) => (prev && prev.pairingId === repair.pairingId
            ? { ...prev, status: 'Completed' }
            : prev));
          refreshCredentials();
          toast.addToast(`Re-paired "${repair.credentialName}" - confirmed after reconnecting. The old credential was revoked.`, 'success');
          return;
        }
        if (status.status === 'Expired' || status.status === 'Cancelled') {
          setRepair((prev) => (prev && prev.pairingId === repair.pairingId ? { ...prev, status: status.status } : prev));
          return;
        }
        setRepair((prev) => {
          if (!prev || prev.pairingId !== repair.pairingId || prev.status !== 'RecoveringCompletion') return prev;
          const attempts = (prev.recoveryAttempts || 0) + 1;
          // Genuinely unresolved, not a known failure: unlike CompletionFailed (where completeRepair
          // itself returned an error, so the old credential is definitely untouched), here the
          // backend never said either way - it may already have succeeded. Must never claim the old
          // credential "was not revoked", which could be false.
          return attempts >= COMPLETION_RECOVERY_MAX_ATTEMPTS ? { ...prev, status: 'CompletionUnknown' } : { ...prev, recoveryAttempts: attempts };
        });
      } catch {
        // A transient failure to even reach the backend - keep trying within the same bound
        // rather than immediately treating a lost network blip as a genuine completion failure.
        setRepair((prev) => {
          if (!prev || prev.pairingId !== repair.pairingId || prev.status !== 'RecoveringCompletion') return prev;
          const attempts = (prev.recoveryAttempts || 0) + 1;
          return attempts >= COMPLETION_RECOVERY_MAX_ATTEMPTS ? { ...prev, status: 'CompletionUnknown' } : { ...prev, recoveryAttempts: attempts };
        });
      }
    };

    check();
    const id = setInterval(check, REPAIR_POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [repair?.pairingId, repair?.status]);

  const handleStartRepair = async (credential) => {
    if (!selectedId) return;
    try {
      // Binds the new pairing session, at creation, to the exact credential it is meant to
      // replace - the backend refuses to complete a re-pair against any other credential (see
      // sdkApi.createPairing's remarks), so this must never be omitted for a re-pair.
      const created = await sdkApi.createPairing(selectedId, sdkType, credential.id);
      setRepair({
        credentialId: credential.id,
        credentialName: credential.name,
        pairingId: created.pairingId,
        code: created.code,
        expiresAt: created.expiresAt,
        status: 'Pending'
      });
    } catch (err) {
      toast.addToast(err?.message || 'Could not start re-pairing', 'error');
    }
  };

  const handleCancelRepair = async () => {
    if (!repair) return;
    if (!POLLING_REPAIR_STATUSES.has(repair.status)) {
      // A terminal state (Completed/CompletionFailed/Expired/Cancelled/ConfirmationTimedOut) has
      // nothing left to cancel server-side - this is just dismissing the local banner.
      setRepair(null);
      return;
    }
    try {
      await sdkApi.revokePairing(repair.pairingId);
      setRepair(null);
    } catch (err) {
      // The backend refuses to cancel a session that has since been redeemed (or otherwise
      // changed) - never pretend cancellation succeeded when it did not. Re-fetch the real
      // current status instead of silently clearing local state out from under it.
      try {
        const status = await sdkApi.getPairingStatus(repair.pairingId);
        setRepair((prev) => (prev && prev.pairingId === repair.pairingId
          ? {
            ...prev,
            status: status.confirmedAt ? prev.status : status.redeemedAt ? 'AwaitingConfirmation' : status.status,
            confirmationDeadline: status.redeemedAt && !prev.confirmationDeadline
              ? Date.now() + AWAITING_CONFIRMATION_TIMEOUT_MS
              : prev.confirmationDeadline
          }
          : prev));
      } catch {
        // Could not even confirm the real status - leave the existing local state as-is rather
        // than guessing, and tell the operator plainly what happened.
      }
      toast.addToast(
        err?.message || 'Could not cancel - the code may already have been redeemed by the application.',
        'error'
      );
    }
  };

  const loadProjects = async () => {
    try {
      const list = await sdkApi.listProjects();
      setProjects(list);
      if (!selectedId && list.length > 0) setSelectedId(list[0].id);
    } catch (err) {
      toast.addToast(err?.message || 'Could not load projects', 'error');
    }
  };

  useEffect(() => { loadProjects(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!selectedId) { setCredentials(null); return; }
    sdkApi.listCredentials(selectedId).then(setCredentials).catch(() => setCredentials([]));
  }, [selectedId]);

  const handleCreateProject = async (e) => {
    e.preventDefault();
    if (!newName.trim()) return;
    setCreating(true);
    try {
      const created = await sdkApi.createProject(newName.trim());
      setNewName('');
      // Append locally rather than re-fetching the list: a re-fetch here can race the initial
      // mount's own loadProjects() call and lose this project if the mount's (now-stale) response
      // resolves second and overwrites this one. The server response already has everything the
      // list view needs except activeCredentials, which is 0 for a project that's brand new.
      setProjects((prev) => [...(prev || []), { ...created, activeCredentials: 0 }]);
      setSelectedId(created.id);
      toast.addToast(`Project "${created.name}" created`, 'success');
    } catch (err) {
      toast.addToast(err?.message || 'Could not create project', 'error');
    } finally {
      setCreating(false);
    }
  };

  const handleGeneratePairing = async () => {
    if (!selectedId) return;
    setPairing(true);
    setPairingResult(null);
    try {
      const result = await sdkApi.createPairing(selectedId, sdkType);
      setPairingResult(result);
    } catch (err) {
      toast.addToast(err?.message || 'Could not generate a pairing code', 'error');
    } finally {
      setPairing(false);
    }
  };

  const handleRevoke = async (credentialId) => {
    try {
      await sdkApi.revokeCredential(selectedId, credentialId);
      setCredentials((prev) => prev.map((c) => (c.id === credentialId ? { ...c, revokedAt: new Date().toISOString() } : c)));
      toast.addToast('Credential revoked', 'info');
    } catch (err) {
      toast.addToast(err?.message || 'Could not revoke credential', 'error');
    }
  };

  return (
    <div className="sdk-pairing">
      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconLink className="w-6 h-6 tone-neutral" /></div>
            <div>
              <h3>Project</h3>
              <p className="section-desc">Pairing codes and credentials belong to a project - the ProjectId your SDK reports telemetry under.</p>
            </div>
          </div>
        </div>

        {projects === null ? (
          <p className="panel-pending-text">Loading projects...</p>
        ) : projects.length === 0 ? (
          <p className="panel-pending-text">No projects yet - create one below.</p>
        ) : (
          <div className="sdk-project-list">
            {projects.map((p) => (
              <button
                key={p.id}
                type="button"
                className={`sdk-project-chip ${selectedId === p.id ? 'active' : ''}`}
                onClick={() => setSelectedId(p.id)}
              >
                {p.name}
                <span className={`status-badge sdk-project-chip-status ${p.activeCredentials > 0 ? 'status-good' : 'status-neutral'}`}>
                  {p.activeCredentials > 0
                    ? `${p.activeCredentials} active credential${p.activeCredentials === 1 ? '' : 's'}`
                    : 'No active credentials'}
                </span>
              </button>
            ))}
          </div>
        )}

        <form className="sdk-new-project-form" onSubmit={handleCreateProject}>
          <input
            className="approval-input"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder="New project name"
            autoComplete="off"
          />
          <button type="submit" className="small-btn" disabled={creating || !newName.trim()}>
            {creating ? 'Creating...' : '+ New Project'}
          </button>
        </form>
      </section>

      {selectedId && (
        <section className="section-card">
          <div className="section-header">
            <div className="section-title-group">
              <div className="section-icon-badge"><IconLink className="w-6 h-6 tone-neutral" /></div>
              <div>
                <h3>Generate a pairing code</h3>
                <p className="section-desc">Single-use, expires in 10 minutes. Redeem once with the selected SDK, then store the returned project key securely. Active credentials do not prove telemetry delivery.</p>
              </div>
            </div>
          </div>

          <div className="sdk-pairing-form">
            <select className="approval-input sdk-sdk-select" value={sdkType} onChange={(e) => setSdkType(e.target.value)}>
              <option value="dotnet">.NET</option>
              <option value="python">Python</option>
            </select>
            <button type="button" className="small-btn" onClick={handleGeneratePairing} disabled={pairing}>
              {pairing ? 'Generating...' : 'Generate Pairing Code'}
            </button>
          </div>

          {pairingResult && (
            <div className="sdk-pairing-result">
              <p className="sdk-step-label">Pairing code (expires {new Date(pairingResult.expiresAt).toLocaleTimeString()})</p>
              <div className="sdk-code-block sdk-pairing-code">
                <button
                  type="button"
                  className="small-btn sdk-code-copy"
                  aria-label="Copy pairing code"
                  onClick={() => copy(pairingResult.code, 'pairing-code')}
                >
                  {copiedKey === 'pairing-code' ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
                </button>
                <pre><code>{pairingResult.code}</code></pre>
              </div>
              <p className="sdk-hint">
                On the target machine: <code>kairon.pair("http://your-kairon:8000", "{pairingResult.code}")</code> (Python) or
                the .NET SDK's <code>KaironPairingClient.PairAsync</code> - either returns the API key and ProjectId to configure the SDK with.
              </p>
            </div>
          )}
        </section>
      )}

      {selectedId && credentials !== null && credentials.length > 0 && (
        <section className="section-card">
          <div className="section-header">
            <div className="section-title-group">
              <div>
                <h3>Issued credentials</h3>
                <p className="section-desc">Every API key ever issued to this project. Revoking one stops it from authenticating immediately.</p>
              </div>
            </div>
          </div>

          <div className="table-responsive">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Prefix</th>
                  <th>Created</th>
                  <th>Status</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {credentials.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td><code className="path-code">{c.keyPrefix}...</code></td>
                    <td>{new Date(c.createdAt).toLocaleString()}</td>
                    <td>{c.revokedAt ? 'Revoked' : 'Active'}</td>
                    <td>
                      <button
                        type="button"
                        className="small-btn"
                        title="Re-pair (issue a fresh credential and revoke this one)"
                        disabled={repair && !TERMINAL_REPAIR_STATUSES.has(repair.status)}
                        onClick={() => handleStartRepair(c)}
                      >
                        <IconRefresh className="w-3 h-3" /> Re-pair
                      </button>
                      {!c.revokedAt && (
                        <button type="button" className="small-btn" title="Revoke" onClick={() => handleRevoke(c.id)}>
                          <IconTrash className="w-3 h-3" />
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {repair && (
        <section className="section-card">
          <div className="section-header">
            <div className="section-title-group">
              <div className="section-icon-badge"><IconRefresh className="w-6 h-6 tone-neutral" /></div>
              <div>
                <h3>Re-pairing "{repair.credentialName}"</h3>
                <p className="section-desc">
                  {repair.status === 'Pending' && 'Waiting for the application to redeem this code.'}
                  {repair.status === 'AwaitingConfirmation' && 'Redeemed - waiting for the application to confirm it received and is using the new credential before the old one is touched.'}
                  {repair.status === 'Completing' && 'Confirmed - completing the re-pair...'}
                  {repair.status === 'RecoveringCompletion' && 'Reconnected - checking whether the re-pair already finished before this page reloaded...'}
                  {repair.status === 'Completed' && 'Complete - a fresh credential is active and the old one has been revoked.'}
                  {repair.status === 'CompletionFailed' && 'The application confirmed the new credential, but completing the re-pair failed.'}
                  {repair.status === 'CompletionUnknown' && "Could not confirm whether the re-pair finished before this page reloaded - it may have already succeeded."}
                  {repair.status === 'Expired' && 'This code expired before it was used. Start again to generate a new one.'}
                  {repair.status === 'Cancelled' && 'Cancelled. The old credential was left untouched.'}
                  {repair.status === 'ConfirmationTimedOut' && 'Redeemed, but the application never confirmed it received the new credential. The old credential was left untouched - safe to start again.'}
                </p>
              </div>
            </div>
          </div>

          {(repair.status === 'Pending' || repair.status === 'AwaitingConfirmation') && (
            <>
              <p className="sdk-step-label">Pairing code (expires {new Date(repair.expiresAt).toLocaleTimeString()})</p>
              <div className="sdk-code-block sdk-pairing-code">
                <button
                  type="button"
                  className="small-btn sdk-code-copy"
                  aria-label="Copy re-pairing code"
                  onClick={() => copy(repair.code, 'repair-code')}
                >
                  {copiedKey === 'repair-code' ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
                </button>
                <pre><code>{repair.code}</code></pre>
              </div>
              <p className="sdk-hint">
                Run the application with this code, e.g. <code>Kairon(pairing_code="{repair.code}")</code> (Python) or{' '}
                <code>new KaironClient(pairingCode: "{repair.code}")</code> (.NET) - it always takes precedence over any
                credential the application already has stored. The old credential stays active until the application
                itself confirms the new one is working.
              </p>
              {repair.status === 'AwaitingConfirmation' && (
                <p className="sdk-hint">Redeemed. Waiting for the application's own confirmation...</p>
              )}
            </>
          )}

          {repair.status === 'CompletionFailed' && (
            <>
              <p className="sdk-hint">
                <IconAlertTriangle className="w-3.5 h-3.5" /> {repair.completionError || 'The re-pair could not be completed.'} The
                old credential was NOT revoked - safe to try again.
              </p>
              <div className="sdk-pairing-form">
                <button type="button" className="small-btn" onClick={() => completeRepairOnce(repair)}>Retry</button>
              </div>
            </>
          )}

          {repair.status === 'CompletionUnknown' && (
            <>
              <p className="sdk-hint">
                <IconAlertTriangle className="w-3.5 h-3.5" /> Could not confirm whether this re-pair actually completed - it may have
                already succeeded, or it may still be waiting to be completed. Retrying is safe either way: completing an
                already-completed re-pair is a no-op and never creates a duplicate credential or runs the replacement twice.
              </p>
              <div className="sdk-pairing-form">
                <button type="button" className="small-btn" onClick={() => completeRepairOnce(repair)}>Retry</button>
              </div>
            </>
          )}

          {(repair.status === 'Expired' || repair.status === 'Cancelled' || repair.status === 'ConfirmationTimedOut') && (
            <p className="sdk-hint">
              <IconAlertTriangle className="w-3.5 h-3.5" /> {repair.status === 'Expired' ? 'Expired' : repair.status === 'Cancelled' ? 'Cancelled' : 'Timed out'} - no credential was changed.
            </p>
          )}

          <div className="sdk-pairing-form">
            <button type="button" className="small-btn" disabled={repair.status === 'Completing'} onClick={handleCancelRepair}>
              {POLLING_REPAIR_STATUSES.has(repair.status) ? 'Cancel' : 'Dismiss'}
            </button>
          </div>
        </section>
      )}
    </div>
  );
}
