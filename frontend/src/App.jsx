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
import { ToastProvider } from './components/Toast';
import { useHealth } from './hooks/useDemo';
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
export default function App() {
  const [tab, setTab] = useState('sre');

  // Component-level health, so the header can say *which* subsystem is down rather than just
  // showing a red dot (frontend PRD section 18).
  const { health } = useHealth();

  const tabs = [
    { id: 'sre', label: 'Overview', icon: <IconShield className="w-4 h-4 text-cyan-400" /> },
    { id: 'services', label: 'Services', icon: <IconDashboard className="w-4 h-4 text-sky-400" /> },
    { id: 'telemetry', label: 'Observability', icon: <IconServer className="w-4 h-4 text-sky-400" /> },
    { id: 'ai-insights', label: 'AI Insights', icon: <IconSparkles className="w-4 h-4 text-amber-400" /> },
    { id: 'remediation', label: 'Remediation', icon: <IconZap className="w-4 h-4 text-emerald-400" /> },
    { id: 'history', label: 'History', icon: <IconHistory className="w-4 h-4 text-emerald-400" /> },
    { id: 'analytics', label: 'Analytics', icon: <IconPredict className="w-4 h-4 text-purple-400" /> },
    { id: 'demo', label: 'Demo Center', icon: <IconAlertTriangle className="w-4 h-4 text-rose-400" /> },
    { id: 'devtools', label: 'Developer Tools', icon: <IconTerminal className="w-4 h-4 text-slate-400" /> },
    { id: 'settings', label: 'Settings', icon: <IconSettings className="w-4 h-4 text-slate-400" /> }
  ];

  return (
    <ToastProvider>
      <div className="app-layout">
        {/* Background glow ambient effects */}
        <div className="ambient-glow glow-1"></div>
        <div className="ambient-glow glow-2"></div>

        {/* Top Navigation Bar */}
        <header className="navbar">
          <div className="nav-container">
            <div className="brand-section" onClick={() => setTab('sre')}>
              <div className="brand-logo-badge">
                <IconShield className="w-6 h-6 text-cyan-400" />
              </div>
              <div className="brand-text">
                <div className="brand-title-wrap">
                  <h1 className="brand-title">Kairon</h1>
                  <span className="version-pill">Autonomous AI SRE</span>
                </div>
                <p className="brand-tagline">AI-Powered DevOps Telemetry & Reliability Platform</p>
              </div>
            </div>

            <div className="status-indicators">
              <div className="status-pill-badge">
                <span className={`status-dot ${health.backend ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">API Core :8000</span>
              </div>
              <div className="status-pill-badge">
                <span className={`status-dot ${health.database ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">Database</span>
              </div>
              <div className="status-pill-badge">
                <span className={`status-dot ${health.aiService ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">
                  AI Microservice{health.aiMode && health.aiMode !== 'unknown' ? ` (${health.aiMode})` : ''}
                </span>
              </div>
              <div className="status-pill-badge">
                <span className={`status-dot ${health.detectionEnabled ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">Detection</span>
              </div>
            </div>
          </div>
        </header>

        {/* Tab Navigation Pill Bar */}
        <div className="nav-tabs-wrapper">
          <nav className="nav-tabs">
            {tabs.map((t) => (
              <button
                key={t.id}
                className={`tab-btn ${tab === t.id ? 'active' : ''}`}
                onClick={() => setTab(t.id)}
              >
                {t.icon}
                <span className="tab-label">{t.label}</span>
              </button>
            ))}
          </nav>
        </div>

        {/* Main Content Area */}
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
        </main>

        {/* Footer */}
        <footer className="footer-bar">
          <span>Kairon DevOps Intelligence Platform &bull; Built for High Reliability & Incident Remediation</span>
          <div className="footer-links">
            <span>FastAPI Python Engine</span>
            <span>&bull;</span>
            <span>.NET 10 Web API Core</span>
            <span>&bull;</span>
            <span>React + Vite</span>
          </div>
        </footer>
      </div>
    </ToastProvider>
  );
}
