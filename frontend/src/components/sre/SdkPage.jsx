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
  // Completed | CompletionFailed, or Expired | Cancelled at any point before those.
  const [repair, setRepair] = useState(null); // { credentialId, credentialName, pairingId, code, expiresAt, status, rebindCount?, completionError? }
  const { copiedKey, copy } = useCopy();
  // Guards against a re-pair completion running more than once: JavaScript callbacks already
  // queued by a prior interval tick can still execute even after clearInterval, so relying on
  // clearInterval alone is not enough to make "observed Confirmed -> complete the repair" happen
  // exactly once (audit Phase 7).
  const completionInFlightRef = useRef(new Set());

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
    if (!repair || (repair.status !== 'Pending' && repair.status !== 'AwaitingConfirmation')) return undefined;
    if (Date.now() >= new Date(repair.expiresAt).getTime()) {
      setRepair((prev) => (prev ? { ...prev, status: 'Expired' } : prev));
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
            ? { ...prev, status: 'AwaitingConfirmation' }
            : prev));
        } else if (Date.now() >= new Date(repair.expiresAt).getTime()) {
          setRepair((prev) => (prev ? { ...prev, status: 'Expired' } : prev));
        }
      } catch {
        // A transient poll failure is not a terminal state - keep polling until expiry.
      }
    }, REPAIR_POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [repair?.pairingId, repair?.status]);

  const handleStartRepair = async (credential) => {
    if (!selectedId) return;
    try {
      const created = await sdkApi.createPairing(selectedId, sdkType);
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
    if (repair.status === 'Pending' || repair.status === 'AwaitingConfirmation') {
      try {
        await sdkApi.revokePairing(repair.pairingId);
      } catch {
        // Best-effort: even if this fails, the code still expires in at most 10 minutes on its own.
      }
    }
    setRepair(null);
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
                        disabled={repair && repair.status === 'Pending'}
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
                  {repair.status === 'Completed' && 'Complete - a fresh credential is active and the old one has been revoked.'}
                  {repair.status === 'CompletionFailed' && 'The application confirmed the new credential, but completing the re-pair failed.'}
                  {repair.status === 'Expired' && 'This code expired before it was used. Start again to generate a new one.'}
                  {repair.status === 'Cancelled' && 'Cancelled. The old credential was left untouched.'}
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

          {(repair.status === 'Expired' || repair.status === 'Cancelled') && (
            <p className="sdk-hint"><IconAlertTriangle className="w-3.5 h-3.5" /> {repair.status === 'Expired' ? 'Expired' : 'Cancelled'} - no credential was changed.</p>
          )}

          <div className="sdk-pairing-form">
            <button type="button" className="small-btn" disabled={repair.status === 'Completing'} onClick={handleCancelRepair}>
              {repair.status === 'Pending' || repair.status === 'AwaitingConfirmation' ? 'Cancel' : 'Dismiss'}
            </button>
          </div>
        </section>
      )}
    </div>
  );
}
