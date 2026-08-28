import React, { useEffect, useMemo, useState } from 'react';
import { getApplications, getMachines } from '../api/agent';
import { platformApi } from '../api/index';

const formatMemory = (bytes = 0) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;
const runtimeSdk = (runtime = '') => runtime.toLowerCase().includes('python') ? 'python' : 'dotnet';

export default function Applications() {
  const [state, setState] = useState({ loading: true, error: null, machines: [], applications: [], platform: [] });
  const [selectedId, setSelectedId] = useState(null);
  const load = async (silent = false) => {
    if (!silent) setState((current) => ({ ...current, loading: true, error: null }));
    try {
      const [machines, applications, platform] = await Promise.all([
        getMachines(), getApplications(), platformApi.getPlatformApplications()
      ]);
      setState({ loading: false, error: null, machines, applications, platform });
      setSelectedId((current) => current || applications[0]?.id || null);
    } catch (error) { setState((current) => ({ ...current, loading: false, error })); }
  };
  useEffect(() => {
    let active = true;
    load();
    const timer = setInterval(() => { if (active) load(true); }, 10000);
    return () => { active = false; clearInterval(timer); };
  }, []);

  const selected = state.applications.find((app) => app.id === selectedId) || null;
  const managed = useMemo(() => selected && state.platform.find((item) =>
    item.name.toLowerCase() === selected.name.toLowerCase() || item.service.toLowerCase() === selected.name.toLowerCase()
  ), [selected, state.platform]);
  if (state.loading) return <div className="empty-state">Loading local Agent status…</div>;
  if (state.error) return <div className="empty-state error-state"><p>{state.error.message || 'Agent status is unavailable.'}</p>
    <button className="btn-secondary" onClick={() => load()}>Retry</button></div>;
  const online = state.machines.filter((machine) => machine.status === 'Online').length;

  return <section className="inventory-page animate-fade-in">
    <div className="inventory-heading"><div><h2>Applications</h2><p>Useful monitoring starts with the Agent. SDK instrumentation is an optional deeper layer.</p></div>
      <span className={`state-badge ${online ? 'healthy' : 'offline'}`}>{online ? `${online} Agent online` : 'Agent offline'}</span></div>
    <div className="monitoring-levels">
      <article><strong>Basic Monitoring</strong><span>✓ Agent · Processes · CPU · Memory · lifecycle</span></article>
      <article><strong>Deep Monitoring</strong><span>✓ Agent + optional SDK · exceptions · HTTP · dependencies · traces</span></article>
    </div>
    {state.applications.length === 0 ? <div className="empty-state">No applications reported yet. Start KAIRON Agent to discover local processes.</div> :
      <div className="inventory-layout"><div className="inventory-table-wrap"><table className="inventory-table">
        <thead><tr><th>Application</th><th>Machine</th><th>Runtime</th><th>Monitoring</th><th>CPU</th><th>Memory</th><th>Status</th></tr></thead>
        <tbody>{state.applications.map((app) => <tr key={app.id} className={selectedId === app.id ? 'selected' : ''} onClick={() => setSelectedId(app.id)}>
          <td><strong>{app.name}</strong><small>PID {app.processId}{app.executable ? ` · ${app.executable}` : ''}</small></td>
          <td>{app.machineName}</td><td>{app.runtime}</td><td>{managed ? 'Deep available' : app.monitoringLevel}</td>
          <td>{Number(app.cpuPercent || 0).toFixed(1)}%</td><td>{formatMemory(app.memoryBytes)}</td>
          <td><span className={`state-badge ${app.isRunning ? 'healthy' : 'offline'}`}>{app.telemetryStatus}</span></td>
        </tr>)}</tbody></table></div>
        {selected && <IntegrationPanel application={selected} managed={managed} onChanged={() => load(true)} />}
      </div>}
  </section>;
}

function IntegrationPanel({ application, managed, onChanged }) {
  const [sdkType, setSdkType] = useState(runtimeSdk(application.runtime));
  const [pairing, setPairing] = useState(null);
  const [installations, setInstallations] = useState([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => { setSdkType(runtimeSdk(application.runtime)); setPairing(null); setError(null); }, [application.id, application.runtime]);
  useEffect(() => {
    if (!managed) { setInstallations([]); return undefined; }
    let active = true;
    const load = () => platformApi.getSdkInstallations(managed.id)
      .then((rows) => { if (active) setInstallations(rows); }).catch((reason) => { if (active) setError(reason); });
    load(); const timer = setInterval(load, 3000);
    return () => { active = false; clearInterval(timer); };
  }, [managed?.id]);
  const beginPairing = async () => {
    setBusy(true); setError(null); setPairing(null);
    try {
      const target = managed || await platformApi.registerDiscoveredApplication(application.id);
      setPairing(await platformApi.createPairing(target.id, sdkType)); onChanged();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const command = pairing ? pairingCommand(sdkType, pairing.code) : '';
  const copy = async () => {
    try { await navigator.clipboard.writeText(command); setCopied(true); }
    catch { setError({ message: 'Clipboard access is unavailable. Select and copy the command manually.' }); }
  };
  const active = installations.filter((item) => item.connected);
  return <aside className="integration-panel">
    <div><span className="eyebrow">Deep Monitoring</span><h3>{application.name}</h3><p>Agent monitoring remains active whether or not an SDK is connected.</p></div>
    <div className="integration-status-row"><span className="state-badge healthy">Agent connected</span>
      <span className={`state-badge ${active.length ? 'healthy' : 'neutral'}`}>{active.length ? `${active.length} SDK connected` : 'SDK not connected'}</span></div>
    <label className="integration-label">SDK runtime<select value={sdkType} onChange={(event) => setSdkType(event.target.value)}>
      <option value="dotnet">.NET SDK 1.0.0</option><option value="python">Python SDK 1.0.0</option></select></label>
    <div className="sdk-requirements"><strong>{sdkType === 'python' ? 'Python 3.10+' : '.NET 10 / ASP.NET Core'}</strong>
      <span>{sdkType === 'python' ? 'Package: kairon-sdk' : 'Package: AIDIP.SDK'}</span></div>
    <button className="btn-primary" disabled={busy} onClick={beginPairing}>{busy ? 'Generating…' : 'Generate pairing code'}</button>
    {pairing && <div className="pairing-box"><div><strong>Temporary pairing code</strong><span>Expires {new Date(pairing.expiresAt).toLocaleTimeString()}</span></div>
      <code>{pairing.code}</code><label>Run in your application environment<textarea readOnly rows="4" value={command} /></label>
      <button className="btn-secondary" onClick={copy}>{copied ? 'Copied' : 'Copy command'}</button>
      <small>The code is scoped to this application, expires in 10 minutes, and can be used once.</small></div>}
    {active.map((item) => <div className="sdk-installation" key={item.id}><div><strong>{item.sdkType} {item.version}</strong>
      <span>{item.lastSeenAt ? 'Telemetry flowing' : 'Paired; waiting for telemetry'}</span></div>
      <button className="btn-text danger" onClick={async () => { await platformApi.revokeSdkInstallation(item.id); setInstallations((rows) => rows.filter((row) => row.id !== item.id)); }}>Revoke</button></div>)}
    {error && <div className="inline-error">{error.message || 'Integration action failed.'}</div>}
    <details><summary>Installation and troubleshooting</summary><p>{sdkType === 'python'
      ? 'Install with pip install kairon-sdk[fastapi], redeem the pairing code, then add KaironMiddleware.'
      : 'Install the AIDIP.SDK NuGet package, redeem the pairing code, then call AddAIDIP and UseAIDIP.'}</p>
      <p>If pairing fails, generate a new code and confirm KAIRON is running locally.</p></details>
  </aside>;
}

function pairingCommand(type, code) {
  return type === 'python'
    ? `from kairon_sdk import KaironClient\npaired = KaironClient.pair("http://127.0.0.1:8000", "${code}")\n# Use paired values in KaironOptions; do not log the credential.`
    : `dotnet add package AIDIP.SDK --version 1.0.0\n# Then call AIDIPPairingClient.PairAsync("http://127.0.0.1:8000", "${code}")`;
}
