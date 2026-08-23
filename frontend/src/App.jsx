import React, { useState, useEffect } from 'react';
import ErrorAnalyzer from './components/ErrorAnalyzer';
import ApiValidator from './components/ApiValidator';
import Predictor from './components/Predictor';
import Recommender from './components/Recommender';
import AuditHistory from './components/AuditHistory';
import DashboardOverview from './components/DashboardOverview';
import TelemetryMonitor from './components/TelemetryMonitor';
import { ToastProvider } from './components/Toast';
import { 
  IconDashboard, 
  IconBug, 
  IconLink, 
  IconPredict, 
  IconSparkles, 
  IconHistory, 
  IconServer, 
  IconShield 
} from './components/Icons';
import API from './api';
import './index.css';

export default function App() {
  const [tab, setTab] = useState('overview');
  const [backendOnline, setBackendOnline] = useState(true);
  const [aiOnline, setAiOnline] = useState(true);

  useEffect(() => {
    const checkHealth = async () => {
      try {
        await API.get('/health');
        setBackendOnline(true);
      } catch (e) {
        setBackendOnline(false);
      }
    };
    checkHealth();
    const interval = setInterval(checkHealth, 15000);
    return () => clearInterval(interval);
  }, []);

  const tabs = [
    { id: 'overview', label: 'Overview', icon: <IconDashboard className="w-4 h-4" /> },
    { id: 'error', label: 'Incident Triage', icon: <IconBug className="w-4 h-4 text-rose-400" /> },
    { id: 'api', label: 'API Drift Guard', icon: <IconLink className="w-4 h-4 text-cyan-400" /> },
    { id: 'predict', label: 'Risk Radar', icon: <IconPredict className="w-4 h-4 text-purple-400" /> },
    { id: 'rec', label: 'Arch Advisor', icon: <IconSparkles className="w-4 h-4 text-amber-400" /> },
    { id: 'history', label: 'Audit History', icon: <IconHistory className="w-4 h-4 text-emerald-400" /> },
    { id: 'telemetry', label: 'Telemetry Monitor', icon: <IconServer className="w-4 h-4 text-sky-400" /> }
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
            <div className="brand-section" onClick={() => setTab('overview')}>
              <div className="brand-logo-badge">
                <IconShield className="w-6 h-6 text-cyan-400" />
              </div>
              <div className="brand-text">
                <div className="brand-title-wrap">
                  <h1 className="brand-title">AIDIP</h1>
                  <span className="version-pill">v2.4 Live</span>
                </div>
                <p className="brand-tagline">AI-Powered DevOps Telemetry & Reliability Platform</p>
              </div>
            </div>

            <div className="status-indicators">
              <div className="status-pill-badge">
                <span className={`status-dot ${backendOnline ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">API Core :8000</span>
              </div>
              <div className="status-pill-badge">
                <span className={`status-dot ${aiOnline ? 'online' : 'offline'}`}></span>
                <span className="status-pill-text">AI Microservice :8001</span>
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
          {tab === 'overview' && <DashboardOverview onSelectTab={setTab} />}
          {tab === 'error' && <ErrorAnalyzer />}
          {tab === 'api' && <ApiValidator />}
          {tab === 'predict' && <Predictor />}
          {tab === 'rec' && <Recommender />}
          {tab === 'history' && <AuditHistory />}
          {tab === 'telemetry' && <TelemetryMonitor />}
        </main>

        {/* Footer */}
        <footer className="footer-bar">
          <span>AIDIP DevOps Intelligence Platform &bull; Built for High Reliability & Incident Remediation</span>
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
