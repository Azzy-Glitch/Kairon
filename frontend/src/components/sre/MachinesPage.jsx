import React, { useEffect, useMemo, useState } from 'react';
import { agentApi } from '../../api';
import DataTable from '../ui/DataTable';
import EmptyState from '../ui/EmptyState';
import { IconServer, IconShield, IconDashboard, IconZap } from '../Icons';

const PAGE_SIZE = 25;

const formatMemory = (bytes = 0) => `${(bytes / 1024 / 1024).toFixed(1)} MB`;

/**
 * "Basic Monitoring" (docs/DESKTOP_SHELL.md, section 46): what the KAIRON Agent has discovered
 * automatically, with no SDK involved. Read-only by design - deep, per-application SDK pairing
 * lives on the SDK page, not here; this page only answers "what machines and processes has the
 * Agent found".
 */
export default function MachinesPage() {
  const [state, setState] = useState({ loading: true, error: null, machines: [], applications: [] });
  const [search, setSearch] = useState('');
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);

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
  const offline = state.machines.length - online;
  const runningApplications = state.applications.filter((a) => a.isRunning).length;
  const stoppedApplications = state.applications.length - runningApplications;

  return (
    <div className="animate-fade-in">
      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconServer className="w-6 h-6 tone-neutral" /></div>
            <div>
              <h3>Machines</h3>
              <p className="section-desc">Every machine running the KAIRON Agent - zero-code monitoring, no SDK required.</p>
            </div>
          </div>
          <span className={`state-badge ${online ? 'healthy' : 'offline'}`}>
            {online ? `${online} online` : 'No Agent online'}
          </span>
        </div>

        <div className="sre-primary-grid machines-summary-grid">
          <MachineStatTile
            icon={<IconServer className="w-5 h-5" />}
            label="Machines"
            value={state.machines.length}
            sub={`${online} online`}
            tone="active"
          />
          <MachineStatTile
            icon={<IconShield className="w-5 h-5" />}
            label="Online"
            value={online}
            sub={`${offline} offline`}
            tone={offline > 0 ? 'attention' : 'good'}
          />
          <MachineStatTile
            icon={<IconDashboard className="w-5 h-5" />}
            label="Applications discovered"
            value={state.applications.length}
            sub={`${runningApplications} running now`}
            tone="active"
          />
          <MachineStatTile
            icon={<IconZap className="w-5 h-5" />}
            label="Running now"
            value={runningApplications}
            sub={`${stoppedApplications} stopped`}
            tone={runningApplications > 0 ? 'good' : 'attention'}
          />
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
                  <th>User Session</th>
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
                    <td><UserSessionBadge status={m.userSessionStatus} /></td>
                    <td>{m.runningApplications}</td>
                    <td>{new Date(m.lastSeenAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <ApplicationsSection
        applications={state.applications}
        search={search}
        onSearchChange={(value) => {
          setSearch(value);
          setVisibleCount(PAGE_SIZE);
        }}
        visibleCount={visibleCount}
        onShowMore={() => setVisibleCount((c) => c + PAGE_SIZE)}
      />
    </div>
  );
}

function MachineStatTile({ icon, label, value, sub, tone = 'neutral' }) {
  return (
    <div className={`primary-stat-card stat-${tone}`}>
      <div className="primary-stat-icon">{icon}</div>
      <div className="primary-stat-body">
        <span className="primary-stat-label">{label}</span>
        <span className="primary-stat-value">{value}</span>
        {sub && <span className="primary-stat-sub">{sub}</span>}
      </div>
    </div>
  );
}

const APPLICATION_COLUMNS = [
  {
    key: 'name',
    label: 'Application',
    sortable: true,
    render: (app) => {
      const detail = `PID ${app.processId}${app.executable ? ` · ${app.executable}` : ''}${
        app.sessionId != null ? ` · Session ${app.sessionId}` : ''
      }${app.userName ? ` · ${app.userName}` : ''}`;
      return (
        <>
          <strong>{app.name}</strong>
          <div className="panel-pending-text machines-app-detail" title={detail}>
            {detail}
          </div>
        </>
      );
    }
  },
  { key: 'machineName', label: 'Machine', sortable: true, priority: 1 },
  {
    key: 'source',
    label: 'Source',
    priority: 2,
    render: (app) => (app.source === 'UserAgent' ? 'User session' : 'Machine')
  },
  { key: 'runtime', label: 'Runtime', priority: 2 },
  {
    key: 'cpuPercent',
    label: 'CPU',
    align: 'right',
    mono: true,
    sortable: true,
    render: (app) => `${Number(app.cpuPercent || 0).toFixed(1)}%`
  },
  {
    key: 'memoryBytes',
    label: 'Memory',
    align: 'right',
    mono: true,
    sortable: true,
    priority: 1,
    render: (app) => formatMemory(app.memoryBytes)
  },
  {
    key: 'isRunning',
    label: 'Status',
    render: (app) => <SeverityDot online={app.isRunning} label={app.isRunning ? 'Running' : 'Stopped'} />
  }
];

/** Search + a page at a time, not all 169+ rows at once (redesign brief section 8: Machines). */
function ApplicationsSection({ applications, search, onSearchChange, visibleCount, onShowMore }) {
  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return applications;
    return applications.filter((app) =>
      [app.name, app.machineName, app.executable, app.userName].some((field) =>
        (field || '').toLowerCase().includes(q)
      )
    );
  }, [applications, search]);

  const visible = filtered.slice(0, visibleCount);

  return (
    <section className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge"><IconServer className="w-6 h-6 tone-neutral" /></div>
          <div>
            <h3>Applications</h3>
            <p className="section-desc">
              Processes the Agent's process watcher currently sees, across every registered machine
              {applications.length > 0 ? ` (${applications.length} total)` : ''}
            </p>
          </div>
        </div>
      </div>

      {applications.length === 0 ? (
        <EmptyState
          icon={<IconServer className="w-10 h-10" />}
          title="No applications reported yet"
          description="Processes appear here once the KAIRON Agent's process watcher discovers them on a registered machine."
        />
      ) : (
        <>
          <input
            type="search"
            className="approval-input machines-search-input"
            placeholder="Search by application, machine, executable or user..."
            value={search}
            onChange={(e) => onSearchChange(e.target.value)}
            aria-label="Search applications"
          />

          {filtered.length === 0 ? (
            <EmptyState
              icon={<IconServer className="w-10 h-10" />}
              title="No matches"
              description={`Nothing matches "${search}". Try a different application, machine or user name.`}
            />
          ) : (
            <>
              <DataTable
                columns={APPLICATION_COLUMNS}
                rows={visible}
                getRowKey={(app) => app.id}
                sortableDefaultKey="cpuPercent"
              />
              {visibleCount < filtered.length && (
                <div className="machines-show-more">
                  <button type="button" className="secondary-btn" onClick={onShowMore}>
                    Show more ({filtered.length - visibleCount} remaining)
                  </button>
                </div>
              )}
            </>
          )}
        </>
      )}
    </section>
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

/** Independent of the machine's own Online/Offline status - see docs/DESKTOP_SHELL.md: a machine
 * can be Online (its Windows Service heartbeat is current) while no interactive user session is
 * running the UserAgent, and vice versa. */
function UserSessionBadge({ status }) {
  if (status === 'NeverConnected') {
    return <span className="panel-pending-text">Not connected</span>;
  }
  return <SeverityDot online={status === 'Online'} label={status} />;
}
