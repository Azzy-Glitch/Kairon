import React from 'react';
import kaironLogo from '../../assets/kairon-logo.png';
import TelemetryMonitor from './components/TelemetryMonitor';
import SreDashboard from './components/sre/SreDashboard';
import IncidentsPage from './components/sre/IncidentsPage';
import ServicesPage from './components/sre/ServicesPage';
import AiInsightsPage from './components/sre/AiInsightsPage';
import RemediationCenterPage from './components/sre/RemediationCenterPage';
import AnalyticsPage from './components/sre/AnalyticsPage';
import AuditHistory from './components/AuditHistory';
import DeveloperTools from './components/sre/DeveloperTools';
import MachinesPage from './components/sre/MachinesPage';
import SdkPage from './components/sre/SdkPage';
import SettingsPage from './components/sre/SettingsPage';
import { ToastProvider } from './components/Toast';
import { SourceFilterProvider } from './lib/SourceFilterContext';
import { ThemeProvider } from './lib/ThemeContext';
import { PollingPreferenceProvider } from './lib/PollingPreferenceContext';
import { useHealth } from './hooks/useHealth';
import { useDashboard } from './hooks/useIncidents';
import { relativeTime } from './services/incidentService';
import { formatAbsoluteTime } from './lib/labels';
import {
  IconDashboard,
  IconAlertTriangle,
  IconPredict,
  IconSparkles,
  IconHistory,
  IconServer,
  IconShield,
  IconZap,
  IconTerminal,
  IconSettings,
  IconLink,
  IconRefresh
} from './components/Icons';
// Design tokens first - every other stylesheet below consumes these custom properties, so they
// must be registered on :root before anything that reads them.
import './styles/tokens.css';
import './styles/components.css';
import './index.css';
// The SRE operator styles live in their own file so the original theme stays readable. Imported
// here rather than via a CSS @import, which would have to precede every other rule to be valid.
import './styles/sre.css';

/**
 * Navigation (Kairon frontend redesign brief, section 3): 13 destinations grouped under four
 * quiet, sentence-case group labels representing the operator's actual workflow, rather than a
 * flat list.
 *
 * "Incidents" is its own standalone page (IncidentsPage): the full filterable feed and detail
 * workspace. Overview keeps only a short "needs attention" glance list that links here, so a real
 * incident is never fully rendered on two tabs at once.
 */
const NAV_GROUPS = [
  {
    label: 'Monitor',
    items: [
      { id: 'overview', label: 'Overview', subtitle: 'Real-time system health and incidents', icon: <IconShield className="w-4 h-4" /> },
      { id: 'services', label: 'Services', subtitle: 'Health and telemetry for every monitored service', icon: <IconDashboard className="w-4 h-4" /> },
      { id: 'telemetry', label: 'Live telemetry', subtitle: 'Raw telemetry and metric submission', icon: <IconServer className="w-4 h-4" /> },
      { id: 'machines', label: 'Machines', subtitle: 'Machines and processes the KAIRON Agent has discovered', icon: <IconServer className="w-4 h-4" /> }
    ]
  },
  {
    label: 'Respond',
    items: [
      { id: 'incidents', label: 'Incidents', subtitle: 'Every incident, its lifecycle and its evidence', icon: <IconAlertTriangle className="w-4 h-4" /> },
      { id: 'actions', label: 'Actions', subtitle: 'Actions awaiting approval and remediation history', icon: <IconZap className="w-4 h-4" /> },
      { id: 'audit', label: 'Audit trail', subtitle: 'Full audit trail of automated actions', icon: <IconHistory className="w-4 h-4" /> }
    ]
  },
  {
    label: 'Analyse',
    items: [
      { id: 'insights', label: 'Insights', subtitle: 'Investigations, confidence and diagnoses across incidents', icon: <IconSparkles className="w-4 h-4" /> },
      { id: 'analytics', label: 'Analytics', subtitle: 'Trends across incidents, causes and outcomes', icon: <IconPredict className="w-4 h-4" /> }
    ]
  },
  {
    label: 'Build',
    items: [
      { id: 'sdk', label: 'Connect an app', subtitle: 'Install, configure and pair a .NET or Python app', icon: <IconLink className="w-4 h-4" /> },
      { id: 'devtools', label: 'Diagnostics', subtitle: 'API validation, error analysis and prediction utilities', icon: <IconTerminal className="w-4 h-4" /> },
      { id: 'settings', label: 'Settings', subtitle: 'Provider, policy and environment configuration', icon: <IconSettings className="w-4 h-4" /> }
    ]
  }
];

const ALL_TABS = NAV_GROUPS.flatMap((g) => g.items);

/** any open critical -> Critical; any open high -> Degraded; otherwise Healthy. Never contradicts
 * the actual incident state the way a bare component-health check can (brief section 3). */
function deriveHealthState(severityDistribution) {
  const dist = severityDistribution || {};
  if ((dist.Critical || 0) > 0) return { label: 'Critical', tone: 'critical' };
  if ((dist.High || 0) > 0) return { label: 'Degraded', tone: 'high' };
  return { label: 'Healthy', tone: 'healthy' };
}

function AppShell() {
  const [tab, setTab] = React.useState('overview');
  const [collapsed, setCollapsed] = React.useState(false);

  // Component-level health, so the sidebar status block can say *which* subsystem is down.
  const { health } = useHealth();
  // Incident-level severity, so the header pill reflects what's actually open rather than just
  // whether the backend/AI/database processes are up.
  const dashboard = useDashboard();

  const active = ALL_TABS.find((t) => t.id === tab) || ALL_TABS[0];
  const overallHealth = deriveHealthState(dashboard.data?.severityDistribution);

  // Forces a re-render each second so "Updated Xs ago" in the header stays live without polling.
  const [, forceTick] = React.useState(0);
  React.useEffect(() => {
    const id = setInterval(() => forceTick((n) => n + 1), 1000);
    return () => clearInterval(id);
  }, []);

  return (
    <div className="app-shell">
      <aside className={`sidebar ${collapsed ? 'collapsed' : ''}`}>
        <button type="button" className="sidebar-brand" onClick={() => setTab('overview')}>
          <span className="sidebar-brand-icon">
            <img src={kaironLogo} alt="Kairon logo" />
          </span>
          {!collapsed && (
            <span className="sidebar-brand-text">
              <span className="sidebar-brand-name">Kairon</span>
              <span className="sidebar-brand-tagline">AI-powered incident response</span>
            </span>
          )}
        </button>

        <nav className="sidebar-nav" aria-label="Primary">
          {NAV_GROUPS.map((group) => (
            <div className="sidebar-nav-group" key={group.label}>
              {!collapsed && <div className="sidebar-nav-group-label">{group.label}</div>}
              {group.items.map((t) => (
                <button
                  key={t.id}
                  type="button"
                  className={`sidebar-nav-item ${tab === t.id ? 'active' : ''}`}
                  onClick={() => setTab(t.id)}
                  title={t.label}
                  aria-current={tab === t.id ? 'page' : undefined}
                >
                  {t.icon}
                  {!collapsed && <span className="tab-label">{t.label}</span>}
                </button>
              ))}
            </div>
          ))}
        </nav>

        <div className="sidebar-footer">
          {!collapsed && (
            <div className="sidebar-status-card">
              <span className="sidebar-status-label">System status</span>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">
                  <span className={`status-dot ${overallHealth.tone === 'healthy' ? 'online' : 'offline'}`}></span>
                  Overall health
                </span>
                <span className={`sidebar-status-row-value tone-${overallHealth.tone}`}>{overallHealth.label}</span>
              </div>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">AI service</span>
                <span className={`sidebar-status-row-value ${health.aiService ? 'good' : 'bad'}`}>
                  {health.aiService ? 'Operational' : 'Offline'}
                  {health.aiService && health.aiMode && (
                    <span className="sidebar-status-mode">{health.aiMode} mode</span>
                  )}
                </span>
              </div>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">Detection</span>
                <span className={`sidebar-status-row-value ${health.detectionEnabled ? 'good' : 'bad'}`}>
                  {health.detectionEnabled ? 'Active' : 'Paused'}
                </span>
              </div>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">Database</span>
                <span className={`sidebar-status-row-value ${health.database ? 'good' : 'bad'}`}>
                  {health.database ? 'Connected' : 'Unreachable'}
                </span>
              </div>
            </div>
          )}

          <button
            type="button"
            className="sidebar-collapse-btn"
            onClick={() => setCollapsed((c) => !c)}
            aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
          >
            {collapsed ? '»' : '« Collapse'}
          </button>
        </div>
      </aside>

      <div className="app-main">
        <header className="topbar">
          <div className="topbar-title">
            <h1>{active.label}</h1>
            <p>{active.subtitle}</p>
          </div>

          <div className="topbar-actions">
            <span
              className="topbar-updated"
              title={dashboard.lastUpdated ? formatAbsoluteTime(dashboard.lastUpdated.toISOString()) : undefined}
            >
              {dashboard.lastUpdated ? `Updated ${relativeTime(dashboard.lastUpdated)}` : 'Updating...'}
            </span>
            <button
              type="button"
              className="topbar-refresh-btn"
              onClick={() => dashboard.reload({ silent: true })}
              disabled={dashboard.isLoading}
              aria-label="Refresh now"
              title="Refresh now"
            >
              <IconRefresh className="w-4 h-4" />
            </button>
            <div className={`topbar-health-pill tone-${overallHealth.tone}`}>
              <span className={`status-dot ${overallHealth.tone === 'healthy' ? 'online' : 'offline'}`}></span>
              Health: {overallHealth.label}
            </div>
          </div>
        </header>

        <main className="main-content">
          {tab === 'overview' && <SreDashboard onOpenIncidents={() => setTab('incidents')} />}
          {tab === 'incidents' && <IncidentsPage />}
          {tab === 'services' && <ServicesPage />}
          {tab === 'telemetry' && <TelemetryMonitor />}
          {tab === 'machines' && <MachinesPage />}
          {tab === 'actions' && <RemediationCenterPage />}
          {tab === 'audit' && <AuditHistory onOpenDiagnostics={() => setTab('devtools')} />}
          {tab === 'insights' && <AiInsightsPage />}
          {tab === 'analytics' && <AnalyticsPage />}
          {tab === 'sdk' && <SdkPage onTelemetry={() => setTab('telemetry')} />}
          {tab === 'devtools' && <DeveloperTools />}
          {tab === 'settings' && <SettingsPage />}

          <footer className="footer-bar">
            <span>Kairon &bull; Autonomous AI SRE &bull; Built for High Reliability & Incident Remediation</span>
            <div className="footer-links">
              <span>FastAPI Python Engine</span>
              <span>&bull;</span>
              <span>.NET 10 Web API Core</span>
              <span>&bull;</span>
              <span>React + Vite</span>
            </div>
          </footer>
        </main>
      </div>
    </div>
  );
}

export default function App() {
  return (
    <ThemeProvider>
      <PollingPreferenceProvider>
        <ToastProvider>
          <SourceFilterProvider>
            <AppShell />
          </SourceFilterProvider>
        </ToastProvider>
      </PollingPreferenceProvider>
    </ThemeProvider>
  );
}
