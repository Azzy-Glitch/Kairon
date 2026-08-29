import React, { useEffect, useState } from 'react';
import { agentApi } from '../../api';
import { IconServer } from '../Icons';

const formatMemory = (bytes = 0) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;

/**
 * "Basic Monitoring" (docs/DESKTOP_SHELL.md, section 46): what the KAIRON Agent has discovered
 * automatically, with no SDK involved. Read-only by design - deep, per-application SDK pairing
 * lives on the SDK page, not here; this page only answers "what machines and processes has the
 * Agent found".
 */
export default function MachinesPage() {
  const [state, setState] = useState({ loading: true, error: null, machines: [], applications: [] });

  const load = async (silent = false) => {
    if (!silent) setState((current) => ({ ...current, loading: true, error: null }));
    try {
      const [machines, applications] = await Promise.all([agentApi.getMachines(), agentApi.getApplications()]);
      setState({ loading: false, error: null, machines, applications });
    } catch (error) {
      setState((current) => ({ ...current, loading: false, error }));
    }
  };

  useEffect(() => {
    let active = true;
    load();
    const timer = setInterval(() => { if (active) load(true); }, 10000);
    return () => { active = false; clearInterval(timer); };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  if (state.loading) {
    return <div className="section-card"><p className="panel-pending-text">Loading Agent status...</p></div>;
  }

  if (state.error) {
    return (
      <div className="section-card">
        <p className="panel-pending-text">{state.error.message || 'Agent status is unavailable.'}</p>
        <button type="button" className="small-btn" onClick={() => load()}>Retry</button>
      </div>
    );
  }

  const online = state.machines.filter((m) => m.status === 'Online').length;

  return (
    <div className="animate-fade-in">
      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconServer className="w-6 h-6 text-slate-500" /></div>
            <div>
              <h3>Machines</h3>
              <p className="section-desc">Every machine running the KAIRON Agent - zero-code monitoring, no SDK required.</p>
            </div>
          </div>
          <span className={`state-badge ${online ? 'healthy' : 'offline'}`}>
            {online ? `${online} online` : 'No Agent online'}
          </span>
        </div>

        {state.machines.length === 0 ? (
          <p className="panel-pending-text">No machines registered yet. Start the KAIRON Agent to register this one.</p>
        ) : (
          <div className="table-responsive">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Host</th>
                  <th>OS</th>
                  <th>Architecture</th>
                  <th>Agent Version</th>
                  <th>Status</th>
                  <th>Running Apps</th>
                  <th>Last Seen</th>
                </tr>
              </thead>
              <tbody>
                {state.machines.map((m) => (
                  <tr key={m.id}>
                    <td>{m.hostName}</td>
                    <td>{m.operatingSystem}</td>
                    <td>{m.architecture}</td>
                    <td>{m.agentVersion}</td>
                    <td><SeverityDot online={m.status === 'Online'} label={m.status} /></td>
                    <td>{m.runningApplications}</td>
                    <td>{new Date(m.lastSeenAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconServer className="w-6 h-6 text-slate-500" /></div>
            <div>
              <h3>Applications</h3>
              <p className="section-desc">Processes the Agent's process watcher currently sees, across every registered machine.</p>
            </div>
          </div>
        </div>

        {state.applications.length === 0 ? (
          <p className="panel-pending-text">No applications reported yet.</p>
        ) : (
          <div className="table-responsive">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Application</th>
                  <th>Machine</th>
                  <th>Runtime</th>
                  <th>CPU</th>
                  <th>Memory</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {state.applications.map((app) => (
                  <tr key={app.id}>
                    <td>
                      <strong>{app.name}</strong>
                      <div className="panel-pending-text">PID {app.processId}{app.executable ? ` · ${app.executable}` : ''}</div>
                    </td>
                    <td>{app.machineName}</td>
                    <td>{app.runtime}</td>
                    <td>{Number(app.cpuPercent || 0).toFixed(1)}%</td>
                    <td>{formatMemory(app.memoryBytes)}</td>
                    <td><SeverityDot online={app.isRunning} label={app.isRunning ? 'Running' : 'Stopped'} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}

function SeverityDot({ online, label }) {
  return (
    <span className="settings-row-value">
      <span className={`status-dot ${online ? 'online' : 'offline'}`} />
      {label}
    </span>
  );
}
