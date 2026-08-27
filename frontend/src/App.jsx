import React, { useState } from 'react';
import AuditHistory from './components/AuditHistory';
import TelemetryMonitor from './components/TelemetryMonitor';
import SreDashboard from './components/sre/SreDashboard';
import DemoRunner from './components/sre/DemoRunner';
import ServicesPage from './components/sre/ServicesPage';
import AiInsightsPage from './components/sre/AiInsightsPage';
import RemediationCenterPage from './components/sre/RemediationCenterPage';
import AnalyticsPage from './components/sre/AnalyticsPage';
import DeveloperTools from './components/sre/DeveloperTools';
import SettingsPage from './components/sre/SettingsPage';
import { ToastProvider, useToast } from './components/Toast';
import { useDemo, useHealth } from './hooks/useDemo';
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
  IconSettings
} from './components/Icons';
import './index.css';
// The SRE operator styles live in their own file so the original theme stays readable. Imported
// here rather than via a CSS @import, which would have to precede every other rule to be valid.
import './styles/sre.css';

/**
 * Navigation (frontend PRD section 5): Overview, Incidents, Services, Observability, AI Insights,
 * Remediation, History, Analytics, Demo Center, Developer Tools, Settings.
 *
 * "Overview" and "Incidents" are deliberately one screen (SreDashboard) rather than two - it already
 * shows system health tiles, the incident feed and the detail pane together, and splitting a working,
 * tested screen in two for a naming technicality is exactly the unnecessary rewrite the PRD's
 * compatibility rules (sections 5, 50) warn against.
 */
const TABS = [
  { id: 'sre', label: 'Overview', subtitle: 'Real-time system health and incidents', icon: <IconShield className="w-4 h-4" /> },
  { id: 'services', label: 'Services', subtitle: 'Health and telemetry for every monitored service', icon: <IconDashboard className="w-4 h-4" /> },
  { id: 'telemetry', label: 'Observability', subtitle: 'Raw telemetry and metric submission', icon: <IconServer className="w-4 h-4" /> },
  { id: 'ai-insights', label: 'AI Insights', subtitle: 'Investigations, confidence and diagnoses across incidents', icon: <IconSparkles className="w-4 h-4" /> },
  { id: 'remediation', label: 'Remediation', subtitle: 'Actions awaiting approval and remediation history', icon: <IconZap className="w-4 h-4" /> },
  { id: 'history', label: 'History', subtitle: 'Full audit trail of automated actions', icon: <IconHistory className="w-4 h-4" /> },
  { id: 'analytics', label: 'Analytics', subtitle: 'Trends across incidents, causes and outcomes', icon: <IconPredict className="w-4 h-4" /> },
  { id: 'demo', label: 'Demo Center', subtitle: 'Run the end-to-end incident simulation', icon: <IconAlertTriangle className="w-4 h-4" /> },
  { id: 'devtools', label: 'Developer Tools', subtitle: 'API validation, error analysis and prediction utilities', icon: <IconTerminal className="w-4 h-4" /> },
  { id: 'settings', label: 'Settings', subtitle: 'Provider, policy and environment configuration', icon: <IconSettings className="w-4 h-4" /> }
];

function AppShell() {
  const [tab, setTab] = useState('sre');
  const [collapsed, setCollapsed] = useState(false);

  // Component-level health, so the header can say *which* subsystem is down rather than just
  // showing a red dot (frontend PRD section 18).
  const { health } = useHealth();
  const demo = useDemo();
  const toast = useToast();

  const active = TABS.find((t) => t.id === tab) || TABS[0];
  const overallHealthy = health.backend && health.database && health.aiService;

  const runSimulation = async () => {
    try {
      await demo.start();
      toast.addToast('Incident simulation started', 'success');
      setTab('demo');
    } catch (err) {
      toast.addToast(err?.message || 'Could not start the simulation', 'error');
    }
  };

  return (
    <div className="app-shell">
      <aside className={`sidebar ${collapsed ? 'collapsed' : ''}`}>
        <div className="sidebar-brand" onClick={() => setTab('sre')}>
          <div className="sidebar-brand-icon">
            <IconShield className="w-5 h-5" />
          </div>
          {!collapsed && (
            <div className="sidebar-brand-text">
              <span className="sidebar-brand-name">Kairon</span>
              <span className="sidebar-brand-tagline">Autonomous AI SRE</span>
            </div>
          )}
        </div>

        <nav className="sidebar-nav">
          {TABS.map((t) => (
            <button
              key={t.id}
              className={`sidebar-nav-item ${tab === t.id ? 'active' : ''}`}
              onClick={() => setTab(t.id)}
              title={t.label}
            >
              {t.icon}
              {!collapsed && <span className="tab-label">{t.label}</span>}
            </button>
          ))}
        </nav>

        <div className="sidebar-footer">
          {!collapsed && (
            <div className="sidebar-status-card">
              <span className="sidebar-status-label">System status</span>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">
                  <span className={`status-dot ${overallHealthy ? 'online' : 'offline'}`}></span>
                  Overall health
                </span>
                <span className={`sidebar-status-row-value ${overallHealthy ? 'good' : 'bad'}`}>
                  {overallHealthy ? 'Healthy' : 'Degraded'}
                </span>
              </div>

              <div className="sidebar-status-row">
                <span className="sidebar-status-row-label">AI service</span>
                <span className={`sidebar-status-row-value ${health.aiService ? 'good' : 'bad'}`}>
                  {health.aiService ? `Operational${health.aiMode ? ` (${health.aiMode})` : ''}` : 'Offline'}
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

          <button className="sidebar-simulate-btn" onClick={runSimulation} disabled={demo.busy === 'start'}>
            <IconAlertTriangle className="w-4 h-4" />
            {!collapsed && (demo.busy === 'start' ? 'Starting...' : 'Run Incident Simulation')}
          </button>

          <button className="sidebar-collapse-btn" onClick={() => setCollapsed((c) => !c)}>
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
            <div className="topbar-health-pill">
              <span className={`status-dot ${overallHealthy ? 'online' : 'offline'}`}></span>
              Health: {overallHealthy ? 'Healthy' : 'Degraded'}
            </div>
          </div>
        </header>

        <main className="main-content">
          {tab === 'sre' && <SreDashboard />}
          {tab === 'services' && <ServicesPage />}
          {tab === 'telemetry' && <TelemetryMonitor />}
          {tab === 'ai-insights' && <AiInsightsPage />}
          {tab === 'remediation' && <RemediationCenterPage />}
          {tab === 'history' && <AuditHistory />}
          {tab === 'analytics' && <AnalyticsPage />}
          {tab === 'demo' && <DemoRunner />}
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
    <ToastProvider>
      <AppShell />
    </ToastProvider>
  );
}
