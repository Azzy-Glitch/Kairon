import React, { useEffect, useState } from 'react';
import { databaseConfigApi } from '../../api';
import Button from '../ui/Button';

const defaults = { provider: 'SQLite', server: '', database: '', authentication: 'Windows', userName: '', password: '', encrypt: true, trustServerCertificate: false };
const label = (provider) => provider === 'SqlServer' ? 'SQL Server' : 'SQLite';

export default function DatabaseSettings() {
  const [form, setForm] = useState(defaults);
  const [summary, setSummary] = useState(null);
  const [busy, setBusy] = useState(false);
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
  async function submit(save) {
    setBusy(true); setMessage(''); setFailed(false);
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
    } finally { setBusy(false); }
  }
  const sql = form.provider === 'SqlServer';
  return <section className="settings-ai-config" aria-labelledby="database-settings-heading">
    <h4 id="database-settings-heading">Database settings</h4>
    <p className="panel-pending-text">SQLite is the default local database. It needs no external database server. SQL Server is optional and must already exist.</p>
    {summary && <p>Active database: <strong>{label(summary.activeProvider)}</strong>{summary.requiresRestart && <> · Saved for restart: <strong>{label(summary.selected.provider)}</strong></>}</p>}
    <div className="database-settings-grid">
      <label>Storage engine
        <select value={form.provider} disabled={!loaded || busy} onChange={(e) => { update('provider', e.target.value); update('password', ''); }}>
          <option value="SQLite">SQLite — local, no server required</option><option value="SqlServer">SQL Server — existing server</option>
        </select>
      </label>
      {sql && <>
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
      </>}
    </div>
    {sql && <p className="panel-pending-text">Windows authentication uses the account running the backend. Production requires encryption with certificate validation. Changing a saved login target requires re-entering its password.</p>}
    <p className="panel-pending-text">Changes apply on backend restart, never during active requests. Switching databases does not copy projects, incidents, credentials or AI settings; each database retains its own data. Restart when remediation is idle. Back up data before switching.</p>
    <div className="settings-ai-config-actions">
      <Button disabled={!loaded || busy} onClick={() => submit(false)}>Test database connection</Button>
      <Button disabled={!loaded || busy} onClick={() => submit(true)}>Save database settings</Button>
    </div>
    {message && <p role="status" className={failed ? 'tone-danger' : 'panel-pending-text'}>{message}</p>}
    <p className="settings-note">Database passwords are encrypted server-side, never returned by this page, and cleared from the form after saving. They are not stored in browser storage or telemetry.</p>
  </section>;
}
