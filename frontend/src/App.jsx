import React, { useState } from 'react';
import ErrorAnalyzer from './components/ErrorAnalyzer';
import ApiValidator from './components/ApiValidator';
import Predictor from './components/Predictor';
import Recommender from './components/Recommender';
import AuditHistory from './components/AuditHistory';
import DashboardOverview from './components/DashboardOverview';
import TelemetryMonitor from './components/TelemetryMonitor';
import SreDashboard from './components/sre/SreDashboard';
import DemoRunner from './components/sre/DemoRunner';
import Applications from './components/Applications';
import SystemStatus from './components/SystemStatus';
import { ToastProvider } from './components/Toast';
import { useHealth } from './hooks/useDemo';
import {
  IconDashboard,
  IconBug,
  IconLink,
  IconPredict,
  IconSparkles,
  IconHistory,
  IconServer,
  IconShield,
  IconZap
} from './components/Icons';
import './index.css';
// The SRE operator styles live in their own file so the original theme stays readable. Imported
// here rather than via a CSS @import, which would have to precede every other rule to be valid.
import './styles/sre.css';

export default function App() {
  const [tab, setTab] = useState('sre');

  // Component-level health, so the header can say *which* subsystem is down rather than just
  // showing a red dot (frontend PRD section 18).
  const { health } = useHealth();

  const tabs = [
    { id: 'sre', label: 'SRE Command', icon: <IconShield className="w-4 h-4 text-cyan-400" /> },
    { id: 'demo', label: 'Incident Simulation', icon: <IconZap className="w-4 h-4 text-amber-400" /> },
    { id: 'overview', label: 'Overview', icon: <IconDashboard className="w-4 h-4" /> },
    { id: 'applications', label: 'Applications', icon: <IconServer className="w-4 h-4 text-emerald-400" /> },
    { id: 'error', label: 'Incident Triage', icon: <IconBug className="w-4 h-4 text-rose-400" /> },
    { id: 'api', label: 'API Drift Guard', icon: <IconLink className="w-4 h-4 text-cyan-400" /> },
    { id: 'predict', label: 'Risk Radar', icon: <IconPredict className="w-4 h-4 text-purple-400" /> },
    { id: 'rec', label: 'Arch Advisor', icon: <IconSparkles className="w-4 h-4 text-amber-400" /> },
    { id: 'history', label: 'Audit History', icon: <IconHistory className="w-4 h-4 text-emerald-400" /> },
    { id: 'telemetry', label: 'Telemetry Monitor', icon: <IconServer className="w-4 h-4 text-sky-400" /> },
    { id: 'settings', label: 'System & Help', icon: <IconServer className="w-4 h-4 text-slate-400" /> }
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
                  <h1 className="brand-title">KAIRON</h1>
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
                <span className="status-pill-text">Local Storage</span>
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
          {tab === 'demo' && <DemoRunner />}
          {tab === 'overview' && <DashboardOverview onSelectTab={setTab} />}
          {tab === 'applications' && <Applications />}
          {tab === 'error' && <ErrorAnalyzer />}
          {tab === 'api' && <ApiValidator />}
          {tab === 'predict' && <Predictor />}
          {tab === 'rec' && <Recommender />}
          {tab === 'history' && <AuditHistory />}
          {tab === 'telemetry' && <TelemetryMonitor />}
          {tab === 'settings' && <SystemStatus />}
        </main>

        {/* Footer */}
        <footer className="footer-bar">
          <span>KAIRON DevOps Intelligence Platform &bull; Built for High Reliability & Incident Remediation</span>
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
