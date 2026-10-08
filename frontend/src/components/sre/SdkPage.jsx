import React, { useEffect, useRef, useState } from 'react';
import { sdkApi } from '../../api';
import { useToast } from '../Toast';
import SdkGuide, { RemediationCard } from './SdkGuide';
import ConnectApp, { DEFAULT_FRAMEWORK_ID, frameworkById } from './ConnectApp';
import { IconCopy, IconCheck, IconTrash, IconRefresh, IconAlertTriangle } from '../Icons';
import { CodeBlock, useCopy } from './CopyableCode';

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

// Every status this UI has ever written to storage - a value outside this set means the blob is
// foreign/corrupted/from a future version this build does not understand, never a state to trust.
const KNOWN_REPAIR_STATUSES = new Set([
  'Pending', 'AwaitingConfirmation', 'Completing', 'RecoveringCompletion',
  'Completed', 'CompletionFailed', 'CompletionUnknown', 'Expired', 'Cancelled', 'ConfirmationTimedOut',
]);

/**
 * Parses and sanitizes whatever is actually in sessionStorage before anything in this component
 * ever sees it. A legacy write from before the pairing code was excluded from storage (see
 * saveStoredRepair's own remarks) - or a value tampered with, corrupted, or foreign to this
 * origin - must never be partially trusted: only an allow-listed set of non-secret fields, each
 * shape-checked, is ever restored, `code` is explicitly stripped even if present, and every other
 * unrecognized field is dropped. The sanitized result is written straight back, so the STORED
 * representation itself stops carrying whatever was rejected, not merely this one in-memory read.
 */
function loadStoredRepair() {
  try {
    const raw = sessionStorage.getItem(REPAIR_STORAGE_KEY);
    if (!raw) return null;

    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;

    const { projectId, credentialId, credentialName, pairingId, expiresAt, status,
      confirmationDeadline, rebindCount, completionError, recoveryAttempts } = parsed;

    // The identifiers/status this whole object is keyed on must themselves be well-formed, or the
    // entire blob is treated as nothing stored - never partially restored around a broken core.
    if (typeof projectId !== 'string' || !projectId) return null;
    if (typeof pairingId !== 'string' || !pairingId) return null;
    if (typeof credentialId !== 'string' || !credentialId) return null;
    if (typeof status !== 'string' || !KNOWN_REPAIR_STATUSES.has(status)) return null;
    if (expiresAt !== undefined && typeof expiresAt !== 'string') return null;
    if (confirmationDeadline !== undefined && typeof confirmationDeadline !== 'number') return null;

    // Explicit allow-list, not "everything except code": an unrecognized field (a stray secret
    // this build doesn't even know the name of, a future version's field, a tampered addition)
    // never survives either, exactly like `code` never does - the same standard applied to any
    // field, named or not.
    const sanitized = { projectId, credentialId, pairingId, status };
    if (typeof credentialName === 'string') sanitized.credentialName = credentialName;
    if (expiresAt !== undefined) sanitized.expiresAt = expiresAt;
    if (confirmationDeadline !== undefined) sanitized.confirmationDeadline = confirmationDeadline;
    if (typeof rebindCount === 'number') sanitized.rebindCount = rebindCount;
    if (typeof completionError === 'string') sanitized.completionError = completionError;
    if (typeof recoveryAttempts === 'number') sanitized.recoveryAttempts = recoveryAttempts;

    try {
      sessionStorage.setItem(REPAIR_STORAGE_KEY, JSON.stringify(sanitized));
    } catch {
      // Best-effort rewrite - a full/blocked sessionStorage must never block returning the
      // already-sanitized in-memory value below.
    }
    return sanitized;
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
    // The one-time pairing code itself must NEVER be persisted to any browser storage - it is the
    // one secret this object ever carries (credentialId/pairingId are opaque identifiers, not
    // secrets; completionError is already the backend's own scrubbed, safe-to-display message).
    // Recovery never needs it: the code's only purpose is redemption by the SDK on a different
    // machine/process, never anything this browser session itself reads back. Kept only in React
    // state (in memory, for as long as the tab displays it uncopied) - a refresh always loses it,
    // by design, exactly like it would for any other secret.
    const { code: _code, ...safeToPersist } = repair;
    sessionStorage.setItem(REPAIR_STORAGE_KEY, JSON.stringify({ projectId, ...safeToPersist }));
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

/**
 * "Connect an app". One framework choice drives everything: the pairing code is generated for that
 * framework's SDK type, the minimal code shown is that framework's, and the reference guide under
 * "Advanced / How it works" follows it too - so a Python developer can never be handed a .NET code
 * (or vice versa) that their SDK would refuse to redeem.
 */
export default function SdkPage({ onTelemetry, onRemediation }) {
  const toast = useToast();
  const [frameworkId, setFrameworkId] = useState(DEFAULT_FRAMEWORK_ID);
  const framework = frameworkById(frameworkId);
  const [projects, setProjects] = useState(null);
  const [selectedProjectId, setSelectedProjectId] = useState(null);
  const [creatingProject, setCreatingProject] = useState(false);
  const connectRef = useRef(null);

  const loadProjects = async () => {
    try {
      const list = await sdkApi.listProjects();
      setProjects(list);
      setSelectedProjectId((current) => current || (list.length > 0 ? list[0].id : null));
    } catch (err) {
      setProjects((prev) => prev || []);
      toast.addToast(err?.message || 'Could not load projects', 'error');
    }
  };

  useEffect(() => { loadProjects(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const createProject = async (name) => {
    setCreatingProject(true);
    try {
      const created = await sdkApi.createProject(name);
      // Append locally rather than re-fetching the list: a re-fetch here can race the initial
      // mount's own loadProjects() call and lose this project if the mount's (now-stale) response
      // resolves second and overwrites this one.
      setProjects((prev) => [...(prev || []), { ...created, activeCredentials: 0 }]);
      setSelectedProjectId(created.id);
      toast.addToast(`Project "${created.name}" created`, 'success');
      return true;
    } catch (err) {
      toast.addToast(err?.message || 'Could not create project', 'error');
      return false;
    } finally {
      setCreatingProject(false);
    }
  };

  // The reference guide's Python/.NET tabs are the same choice as the framework picker: switching
  // there picks that SDK's default framework here, never a second, independent selection.
  const setGuidePlatform = (sdkType) => {
    if (sdkType !== framework.sdkType) setFrameworkId(sdkType === 'python' ? 'fastapi' : 'aspnetcore');
  };

  const goToPairing = () => {
    connectRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
  };

  return (
    <div className="animate-fade-in sdk-connect">
      <div ref={connectRef}>
        <ConnectApp
          CodeBlock={CodeBlock}
          framework={framework}
          onFrameworkChange={setFrameworkId}
          projects={projects}
          selectedProjectId={selectedProjectId}
          onSelectProject={setSelectedProjectId}
          onCreateProject={createProject}
          creatingProject={creatingProject}
          onTelemetry={onTelemetry}
        />
      </div>

      <RemediationCard onRemediation={onRemediation} />

      <details className="section-card sdk-guide-details sdk-how-it-works">
        <summary>Advanced / How it works</summary>
        <p className="sdk-hint">
          Credentials, re-pairing, manual configuration and the full SDK reference. You do not need any of this to connect an
          application with the steps above.
        </p>
        <div className="sdk-get-started">
          <Pairing projects={projects} selectedId={selectedProjectId} sdkType={framework.sdkType} />
          <SdkGuide
            CodeBlock={CodeBlock}
            platform={framework.sdkType}
            onPlatformChange={setGuidePlatform}
            onPairing={goToPairing}
            onTelemetry={onTelemetry}
          />
        </div>
      </details>
    </div>
  );
}

/**
 * Credentials and re-pairing for the project selected in the Connect flow (lives under "Advanced /
 * How it works"). First-time pairing is the main Connect flow; this only manages what it produced.
 *
 * sdkType: the SDK type of the framework currently selected - used for a re-pair only when the
 * credential being replaced does not itself record which SDK it was issued to.
 */
function Pairing({ projects, selectedId, sdkType }) {
  const toast = useToast();
  const [credentials, setCredentials] = useState(null);
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
      // Distinguish a DEFINITIVE backend answer from a genuinely UNKNOWN outcome. client.js's
      // toOperatorError already makes this distinction for every request: 'timeout'/'offline' mean
      // no response was ever received - the request may still have reached the backend and
      // succeeded before the response was lost (a slow network, a reset connection, a timeout) - so
      // it would be a lie to claim CompletionFailed ("the old credential was left untouched") here;
      // we simply do not know. Every OTHER kind means the backend itself responded (its own
      // CompleteRepair outcomes are all synchronous and transactional - a real response means
      // nothing was persisted), which is the one case safe to report as a definitive failure.
      // Route the uncertain case through the exact same read-only recovery this UI already uses
      // for a refresh mid-completion, rather than inventing a second mechanism - never re-call
      // completeRepair here, only ask the backend for authoritative status.
      if (err?.kind === 'timeout' || err?.kind === 'offline') {
        setRepair((prev) => (prev && prev.pairingId === current.pairingId
          ? { ...prev, status: 'RecoveringCompletion', recoveryAttempts: 0 }
          : prev));
        toast.addToast('Lost the response for that request - checking with the server whether it actually completed...', 'info');
      } else {
        // Never claim the old credential was revoked when this call did not actually succeed - the
        // backend only revokes it as part of this same request completing successfully.
        setRepair((prev) => (prev && prev.pairingId === current.pairingId
          ? { ...prev, status: 'CompletionFailed', completionError: err?.message }
          : prev));
        toast.addToast(
          err?.message || 'The new credential is active, but completing the re-pair failed - the old credential was left untouched. Try again.',
          'error'
        );
      }
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

    // NB-002: self-scheduling setTimeout, not setInterval - the next poll is scheduled only after
    // this one's request (and any awaited completion work) has actually finished, so a slow
    // response can never overlap with a second, already-fired tick starting a concurrent request
    // for the same pairing. REPAIR_POLL_INTERVAL_MS is the gap AFTER each attempt finishes, not a
    // fixed wall-clock cadence - exactly the "request -> wait for result -> wait -> next request"
    // contract this must follow.
    let cancelled = false;
    let timeoutId;

    const poll = async () => {
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
      } finally {
        if (!cancelled) timeoutId = setTimeout(poll, REPAIR_POLL_INTERVAL_MS);
      }
    };

    timeoutId = setTimeout(poll, REPAIR_POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      clearTimeout(timeoutId);
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
    let timeoutId;

    // NB-002: same self-scheduling setTimeout rationale as the polling effect above - the next
    // recovery check is scheduled only once this one's request has actually finished.
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
      } finally {
        if (!cancelled) timeoutId = setTimeout(check, REPAIR_POLL_INTERVAL_MS);
      }
    };

    check();
    return () => {
      cancelled = true;
      clearTimeout(timeoutId);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [repair?.pairingId, repair?.status]);

  const handleStartRepair = async (credential) => {
    if (!selectedId) return;
    try {
      // Binds the new pairing session, at creation, to the exact credential it is meant to
      // replace - the backend refuses to complete a re-pair against any other credential (see
      // sdkApi.createPairing's remarks), so this must never be omitted for a re-pair.
      // A re-pair must be redeemable by the SAME application the credential belongs to, so the
      // original credential's SDK type wins whenever it is known.
      const repairSdkType = credential.sdkType || sdkType;
      const created = await sdkApi.createPairing(selectedId, repairSdkType, credential.id);
      setRepair({
        credentialId: credential.id,
        credentialName: credential.name,
        pairingId: created.pairingId,
        code: created.code,
        expiresAt: created.expiresAt,
        sdkType: created.sdkType || repairSdkType,
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

  useEffect(() => {
    if (!selectedId) { setCredentials(null); return; }
    sdkApi.listCredentials(selectedId).then(setCredentials).catch(() => setCredentials([]));
  }, [selectedId]);

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
      {!selectedId && (
        <p className="panel-pending-text">
          {projects === null ? 'Loading projects...' : 'Select or create a project in step 2 to manage its credentials.'}
        </p>
      )}

      {selectedId && credentials !== null && credentials.length === 0 && (
        <p className="panel-pending-text">No credentials have been issued for this project yet.</p>
      )}

      {selectedId && credentials !== null && credentials.length > 0 && (
        <section className="section-card">
          <div className="section-header">
            <div className="section-title-group">
              <div>
                <h3>Issued credentials{selectedProjectName(projects, selectedId) ? ` - ${selectedProjectName(projects, selectedId)}` : ''}</h3>
                <p className="section-desc">Every credential issued to this project (only a short prefix is shown, never the key). Revoking one stops it from authenticating immediately. Re-pair issues a fresh credential to the same application and revokes the old one only once the application confirms.</p>
              </div>
            </div>
          </div>

          <div className="table-responsive">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Prefix</th>
                  <th>SDK</th>
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
                    <td>{c.sdkType === 'python' ? 'Python' : c.sdkType === 'dotnet' ? '.NET' : '--'}</td>
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
                  {repair.status === 'RecoveringCompletion' && 'Checking whether the re-pair already finished - its response was lost (a refresh, a dropped connection, or a timeout).'}
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
              {repair.code ? (
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
                    Run the application with this code, e.g.{' '}
                    {repair.sdkType === 'dotnet'
                      ? <code>await builder.Services.AddKaironAsync("{repair.code}")</code>
                      : <code>Kairon.attach(app, pairing_code="{repair.code}")</code>}
                    {' '}({repair.sdkType === 'dotnet' ? '.NET' : 'Python'} SDK) - it always takes precedence over any
                    credential the application already has stored. The old credential stays active until the application
                    itself confirms the new one is working.
                  </p>
                </>
              ) : (
                // Recovered after a refresh: the code itself is never persisted (see
                // saveStoredRepair's remarks), only this session's non-secret status. If the
                // application already redeemed it before the refresh, polling below still detects
                // that; if it never got the chance, the code is genuinely gone and a fresh one is
                // needed - never reconstructed from storage.
                <p className="sdk-hint">
                  <IconAlertTriangle className="w-3.5 h-3.5" /> The pairing code isn't shown after a refresh - it is never saved
                  for security. Still watching this session in case the application already redeemed it; cancel and generate a
                  new code if it hasn't.
                </p>
              )}
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

function selectedProjectName(projects, id) {
  return (projects || []).find((p) => p.id === id)?.name || null;
}
