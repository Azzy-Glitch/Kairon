import React, { useEffect, useState } from 'react';
import { getApplications, getMachines } from '../api/agent';

const formatMemory = (bytes) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;

export default function Applications() {
  const [state, setState] = useState({ loading: true, error: null, machines: [], applications: [] });

  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const [machines, applications] = await Promise.all([getMachines(), getApplications()]);
        if (active) setState({ loading: false, error: null, machines, applications });
      } catch (error) {
        if (active) setState((current) => ({ ...current, loading: false, error }));
      }
    };
    load();
    const timer = setInterval(load, 10000);
    return () => { active = false; clearInterval(timer); };
  }, []);

  if (state.loading) return <div className="empty-state">Loading local Agent status…</div>;
  if (state.error) return <div className="empty-state error-state">{state.error.message || 'Agent status is unavailable.'}</div>;
  const online = state.machines.filter((machine) => machine.status === 'Online').length;

  return <section className="inventory-page animate-fade-in">
    <div className="inventory-heading">
      <div><h2>Applications</h2><p>Agent-based monitoring works without an SDK. Add an SDK later for deep application evidence.</p></div>
      <span className={`state-badge ${online ? 'healthy' : 'offline'}`}>{online ? `${online} Agent online` : 'Agent offline'}</span>
    </div>
    <div className="monitoring-levels">
      <article><strong>Basic Monitoring</strong><span>Agent · Processes · CPU · Memory · lifecycle</span></article>
      <article><strong>Deep Monitoring</strong><span>Agent + optional SDK · exceptions · HTTP · dependencies · traces</span></article>
    </div>
    {state.applications.length === 0 ? <div className="empty-state">No applications reported yet. Start KAIRON Agent to discover local processes.</div> :
      <div className="inventory-table-wrap"><table className="inventory-table">
        <thead><tr><th>Application</th><th>Machine</th><th>Runtime</th><th>Monitoring</th><th>CPU</th><th>Memory</th><th>Status</th></tr></thead>
        <tbody>{state.applications.map((app) => <tr key={app.id}>
          <td><strong>{app.name}</strong><small>PID {app.processId}{app.executable ? ` · ${app.executable}` : ''}</small></td>
          <td>{app.machineName}</td><td>{app.runtime}</td><td>{app.monitoringLevel}</td>
          <td>{app.cpuPercent.toFixed(1)}%</td><td>{formatMemory(app.memoryBytes)}</td>
          <td><span className={`state-badge ${app.isRunning ? 'healthy' : 'offline'}`}>{app.telemetryStatus}</span></td>
        </tr>)}</tbody>
      </table></div>}
  </section>;
}
