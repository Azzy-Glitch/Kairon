import React, { useEffect, useState } from 'react';
import { databaseConfigApi } from '../../api';
import Button from '../ui/Button';
import Badge from '../ui/Badge';
import { IconServer, IconTerminal, IconCheck, IconShield, IconAlertTriangle } from '../Icons';

const defaults = { provider: 'SQLite', server: '', database: '', authentication: 'Windows', userName: '', password: '', encrypt: true, trustServerCertificate: false };
const label = (provider) => provider === 'SqlServer' ? 'SQL Server' : 'SQLite';

const ENGINES = [
  { id: 'SQLite', name: 'SQLite', icon: IconTerminal, tagline: 'Local database', detail: 'No external server required' },
  { id: 'SqlServer', name: 'SQL Server', icon: IconServer, tagline: 'Existing database server', detail: 'Requires an already configured SQL Server' }
];

const SWITCHING_NOTES = [
  'Changes apply after backend restart.',
  'Changes never occur during active requests.',
  'Switching databases does not copy projects, incidents, credentials, or AI settings.',
  'Each database retains its own data.',
  'Restart when remediation is idle.',
  'Back up data before switching.'
];

export default function DatabaseSettings() {
  const [form, setForm] = useState(defaults);
  const [summary, setSummary] = useState(null);
  const [pendingAction, setPendingAction] = useState(null);
  const [message, setMessage] = useState('');
  const [failed, setFailed] = useState(false);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    let cancelled = false;
    databaseConfigApi.getConfig().then((data) => {
      if (cancelled) return;
      setSummary(data); setForm({ ...defaults, ...data.selected, password: '' }); setLoaded(true);
    }).catch(() => { if (!cancelled) { setFailed(true); setMessage('Database settings could not be loaded. Check backend access.'); } });
    return () => { cancelled = true; };
  }, []);

  function update(name, value) {
    setForm((current) => ({ ...current, [name]: value }));
    setMessage(''); setFailed(false);
  }
  function selectEngine(provider) {
    if (form.provider === provider) return;
    update('provider', provider);
    update('password', '');
  }
  async function submit(save) {
    setPendingAction(save ? 'save' : 'test'); setMessage(''); setFailed(false);
    try {
      const payload = form.provider === 'SQLite' ? { provider: 'SQLite' } : {
        provider: form.provider, server: form.server, database: form.database, authentication: form.authentication,
        userName: form.userName, password: form.authentication === 'SqlLogin' ? form.password : null,
        encrypt: form.encrypt, trustServerCertificate: form.trustServerCertificate
      };
      if (save) {
        const data = await databaseConfigApi.saveConfig(payload);
        setSummary(data); setForm({ ...defaults, ...data.selected, password: '' });
        setMessage(data.requiresRestart ? 'Saved securely. Restart the KAIRON backend to apply these database settings.' : 'Database settings saved. This selection is already active.');
      } else {
        const result = await databaseConfigApi.testConnection(payload);
        setFailed(!result.success); setMessage(result.message);
      }
    } catch {
      // Do not render/retain an Axios error or a server error body that might contain submitted credentials.
      setFailed(true); setMessage('The operation could not be completed. Check the connection details, certificate settings and operator access.');
    } finally { setPendingAction(null); }
  }
  const busy = pendingAction !== null;
  const sql = form.provider === 'SqlServer';
  return <section className="settings-ai-config" aria-labelledby="database-settings-heading">
    <div className="settings-ai-config-head">
      <span className="settings-row-label" id="database-settings-heading">Database settings</span>
      <p className="panel-pending-text">Choose where KAIRON stores its application data.</p>
    </div>

    {summary && (
      <div className="database-status-card">
        <div>
          <div className="database-status-card-label">Active database</div>
          <div className="database-status-card-value">{label(summary.activeProvider)}</div>
          {summary.requiresRestart && (
            <div className="database-status-card-pending">Saved for next restart: <strong>{label(summary.selected.provider)}</strong></div>
          )}
        </div>
        <span className="database-status-pill"><span className="status-dot online" aria-hidden="true" />Active</span>
      </div>
    )}

    <div className="database-engine-section">
      <span className="settings-row-label" id="storage-engine-label">Storage engine</span>
      <div className="database-engine-grid" role="group" aria-labelledby="storage-engine-label">
        {ENGINES.map((engine) => {
          const Icon = engine.icon;
          const selected = form.provider === engine.id;
          return (
            <button
              key={engine.id}
              type="button"
              className={`database-engine-card${selected ? ' selected' : ''}`}
              aria-pressed={selected}
              aria-label={engine.name}
              disabled={!loaded || busy}
              onClick={() => selectEngine(engine.id)}
            >
              {selected && <IconCheck className="w-4 h-4 database-engine-card-check" aria-hidden="true" />}
              <span className="database-engine-card-icon"><Icon className="w-5 h-5" /></span>
              <h4>{engine.name}</h4>
              <p>{engine.tagline}</p>
              <p>{engine.detail}</p>
            </button>
          );
        })}
      </div>
    </div>

    {sql && (
      <div className="database-settings-grid">
        <label>SQL Server address<input autoComplete="off" value={form.server} disabled={busy} onChange={(e) => update('server', e.target.value)} placeholder="server.example.com or server\instance" /></label>
        <label>Existing database name<input autoComplete="off" value={form.database} disabled={busy} onChange={(e) => update('database', e.target.value)} /></label>
        <label>Database authentication<select value={form.authentication} disabled={busy} onChange={(e) => { update('authentication', e.target.value); update('password', ''); }}>
          <option value="Windows">Windows authentication (backend account)</option><option value="SqlLogin">SQL Server login</option>
        </select></label>
        {form.authentication === 'SqlLogin' && <>
          <label>Database user name<input autoComplete="off" value={form.userName} disabled={busy} onChange={(e) => update('userName', e.target.value)} /></label>
          <label>Database password<input type="password" autoComplete="new-password" data-private="true" value={form.password} disabled={busy} onChange={(e) => update('password', e.target.value)} placeholder={summary?.selected?.hasPassword ? 'Stored password — leave blank for the same connection' : 'Enter password'} /></label>
        </>}
        <label><input type="checkbox" checked={form.encrypt} disabled={busy} onChange={(e) => update('encrypt', e.target.checked)} /> Encrypt the connection</label>
        <label><input type="checkbox" checked={form.trustServerCertificate} disabled={busy} onChange={(e) => update('trustServerCertificate', e.target.checked)} /> Trust a self-signed server certificate (non-production only)</label>
      </div>
    )}
    {sql && <p className="panel-pending-text">Windows authentication uses the account running the backend. Production requires encryption with certificate validation. Changing a saved login target requires re-entering its password.</p>}

    <div className="database-callout database-callout-info" role="note">
      <IconAlertTriangle className="w-5 h-5 database-callout-icon" aria-hidden="true" />
      <ul>
        {SWITCHING_NOTES.map((note) => <li key={note}>{note}</li>)}
      </ul>
    </div>

    <div className="settings-ai-config-actions">
      <Button variant="secondary" disabled={!loaded || busy} onClick={() => submit(false)}>
        {pendingAction === 'test' ? 'Testing...' : 'Test database connection'}
      </Button>
      <Button variant="primary" disabled={!loaded || busy} onClick={() => submit(true)}>
        {pendingAction === 'save' ? 'Saving...' : 'Save database settings'}
      </Button>
    </div>
    {message && (
      <div className="settings-ai-config-result" role="status">
        <Badge tone={failed ? 'critical' : 'healthy'}>{failed ? 'Error' : 'Success'}</Badge>
        <span className="panel-pending-text">{message}</span>
      </div>
    )}

    <div className="database-callout database-callout-security">
      <IconShield className="w-5 h-5 database-callout-icon" aria-hidden="true" />
      <p>Database passwords are encrypted server-side, never returned by this page, and cleared from the form after saving. They are not stored in browser storage or telemetry.</p>
    </div>
  </section>;
}
