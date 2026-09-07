import React, { useEffect, useState } from 'react';
import { sdkApi } from '../../api';
import { useToast } from '../Toast';
import Tabs from '../ui/Tabs';
import SdkGuide from './SdkGuide';
import { IconLink, IconCopy, IconCheck, IconTrash } from '../Icons';

const VIEWS = [
  { id: 'start', label: 'Get Started' },
  { id: 'pairing', label: 'Pairing' }
];

// SDK guide content is checked against the shipped SDK APIs. Pairing remains live.
export default function SdkPage({ onTelemetry }) {
  const [view, setView] = useState('start');

  return (
    <div className="animate-fade-in">
      <Tabs items={VIEWS} activeId={view} onChange={setView} className="dev-tools-subnav" />

      {view === 'start' && <SdkGuide CodeBlock={CodeBlock} onPairing={() => setView('pairing')} onTelemetry={onTelemetry} />}
      {view === 'pairing' && <Pairing />}
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

function Pairing() {
  const toast = useToast();
  const [projects, setProjects] = useState(null);
  const [selectedId, setSelectedId] = useState(null);
  const [newName, setNewName] = useState('');
  const [creating, setCreating] = useState(false);
  const [credentials, setCredentials] = useState(null);
  const [sdkType, setSdkType] = useState('dotnet');
  const [pairing, setPairing] = useState(false);
  const [pairingResult, setPairingResult] = useState(null);
  const { copiedKey, copy } = useCopy();

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
    </div>
  );
}
