import React, { useEffect, useState } from 'react';
import { sdkApi } from '../../api';
import { useToast } from '../Toast';
import { IconLink, IconCopy, IconCheck, IconTrash } from '../Icons';

/**
 * SDK integration and pairing (docs/DESKTOP_SHELL.md, frontend PRD-style section 21-22 from the
 * productization spec): a developer who installs Kairon should be able to find "how do I connect
 * my app" without leaving the app.
 *
 * "Get Started" reuses the already-written docs verbatim (README's SDK integration section,
 * sdk-python/README.md) - no new documentation content invented here. "Pairing" is the live
 * counterpart: generate a real, working, short-lived pairing code an SDK can redeem for a
 * persistent credential. That credential is shown here exactly once, at creation - the same
 * one-time-reveal pattern GitHub/AWS use for access tokens, not a violation of "secrets are never
 * stored in the frontend" (Settings' own stated rule): nothing here is persisted client-side,
 * it's a transient value in component state until the page is left or refreshed.
 */
export default function SdkPage() {
  const [view, setView] = useState('start');

  return (
    <div className="animate-fade-in">
      <div className="dev-tools-subnav">
        <button
          type="button"
          className={`tab-btn dev-tools-tab ${view === 'start' ? 'active' : ''}`}
          onClick={() => setView('start')}
        >
          <IconLink className="w-4 h-4" />
          <span className="tab-label">Get Started</span>
        </button>
        <button
          type="button"
          className={`tab-btn dev-tools-tab ${view === 'pairing' ? 'active' : ''}`}
          onClick={() => setView('pairing')}
        >
          <IconLink className="w-4 h-4" />
          <span className="tab-label">Pairing</span>
        </button>
      </div>

      {view === 'start' && <GetStarted />}
      {view === 'pairing' && <Pairing />}
    </div>
  );
}

function useCopy() {
  const toast = useToast();
  const [copiedKey, setCopiedKey] = useState(null);

  const copy = (text, key) => {
    navigator.clipboard.writeText(text);
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
      <button type="button" className="small-btn sdk-code-copy" onClick={() => copy(code, copyKey)}>
        {copiedKey === copyKey ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
      </button>
      <pre><code>{code}</code></pre>
    </div>
  );
}

const DOTNET_INSTALL = `dotnet add package Kairon.SDK`;

const DOTNET_USAGE = `builder.Services.AddKairon(options =>
{
    options.Endpoint = "http://localhost:8000";
    options.ProjectId = Guid.Parse("...");
    options.ServiceName = "OrderProcessingService";
});

app.UseKairon();`;

const PYTHON_INSTALL = `pip install -e .              # core client, stdlib-only
pip install -e .[fastapi]     # + KaironMiddleware for FastAPI/Starlette`;

const PYTHON_USAGE = `from kairon import Kairon

kairon = Kairon(
    endpoint="https://your-kairon-server",
    project_id="my-project",
    service="OrderProcessingService",
)
kairon.start()`;

const PYTHON_FASTAPI = `from kairon.middleware import KaironMiddleware

app.add_middleware(KaironMiddleware)`;

function GetStarted() {
  return (
    <div className="sdk-get-started">
      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconLink className="w-6 h-6 text-slate-500" /></div>
            <div>
              <h3>.NET SDK</h3>
              <p className="section-desc">Two lines of integration in an ASP.NET Core application.</p>
            </div>
          </div>
        </div>
        <p className="sdk-step-label">Install</p>
        <CodeBlock code={DOTNET_INSTALL} copyKey="dotnet-install" />
        <p className="sdk-step-label">Configure</p>
        <CodeBlock code={DOTNET_USAGE} copyKey="dotnet-usage" />
        <p className="sdk-hint">
          Get a real Endpoint, ProjectId and API key for your application on the Pairing tab
          instead of hand-copying values.
        </p>
      </section>

      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconLink className="w-6 h-6 text-slate-500" /></div>
            <div>
              <h3>Python SDK</h3>
              <p className="section-desc">Stdlib-only core client; FastAPI middleware is a separate import.</p>
            </div>
          </div>
        </div>
        <p className="sdk-step-label">Install</p>
        <CodeBlock code={PYTHON_INSTALL} copyKey="python-install" />
        <p className="sdk-step-label">Configure</p>
        <CodeBlock code={PYTHON_USAGE} copyKey="python-usage" />
        <p className="sdk-step-label">FastAPI integration (optional)</p>
        <CodeBlock code={PYTHON_FASTAPI} copyKey="python-fastapi" />
      </section>
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
            <div className="section-icon-badge"><IconLink className="w-6 h-6 text-slate-500" /></div>
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
                <span className="sdk-project-chip-count">{p.activeCredentials} active key{p.activeCredentials === 1 ? '' : 's'}</span>
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
              <div className="section-icon-badge"><IconLink className="w-6 h-6 text-slate-500" /></div>
              <div>
                <h3>Generate a pairing code</h3>
                <p className="section-desc">Single-use, expires in 10 minutes. The SDK redeems it once for a persistent API key.</p>
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
