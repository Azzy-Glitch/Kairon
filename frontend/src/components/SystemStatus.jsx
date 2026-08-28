import React from 'react';
import { useHealth } from '../hooks/useDemo';

export default function SystemStatus() {
  const { health, isLoading, isError, reload } = useHealth();
  return <section className="system-page animate-fade-in">
    <div className="inventory-heading"><div><h2>System & Settings</h2><p>Local runtime health and integration help. Database administration is managed by KAIRON.</p></div>
      <button className="btn-secondary" onClick={() => reload()} disabled={isLoading}>Refresh</button></div>
    {isError && <div className="inline-error">KAIRON services are unreachable. Restart KAIRON and try again.</div>}
    <div className="system-health-grid">
      <HealthCard title="Local Storage" healthy={health.database}
        detail={health.database ? `${health.persistenceProvider || 'SQLite'} · ${formatBytes(health.databaseSizeBytes)} · raw telemetry ${health.rawTelemetryRetentionDays || 14} days · maintenance ${health.maintenanceStatus || 'pending'}`
          : 'Unavailable · monitoring data cannot be persisted'} />
      <HealthCard title="Backend" healthy={health.backend} detail={health.backend ? 'Local API ready' : 'Unavailable'} />
      <HealthCard title="AI Gateway" healthy={health.aiService}
        detail={health.aiService ? `${health.aiMode || 'configured'} mode` : 'Unavailable · incidents continue without AI'} />
      <HealthCard title="Detection" healthy={health.detectionEnabled}
        detail={health.detectionEnabled ? 'Incident evaluation active' : 'Disabled'} />
      <HealthCard title="Remediation" healthy={health.remediationEnabled}
        detail={health.remediationEnabled ? 'Approval-controlled actions enabled' : 'Disabled'} />
    </div>
    <div className="help-grid">
      <article><h3>Basic monitoring</h3><p>Start the KAIRON Agent. Applications appear automatically with process, CPU, memory, and lifecycle signals.</p></article>
      <article><h3>Deep monitoring</h3><p>Open Applications, select a process, choose .NET or Python, and generate a temporary pairing code.</p></article>
      <article><h3>Troubleshooting</h3><p>If telemetry stops, confirm the Agent is online, the SDK installation is not revoked, and Local Storage is healthy.</p></article>
      <article><h3>Security</h3><p>Credentials are scoped and redacted. Remediation remains approval-based and only registered actions can execute.</p></article>
    </div>
  </section>;
}

function formatBytes(value) {
  if (value == null) return 'externally managed';
  if (value < 1024 * 1024) return `${Math.max(1, Math.round(value / 1024))} KB`;
  return `${(value / 1024 / 1024).toFixed(1)} MB`;
}

function HealthCard({ title, healthy, detail }) {
  return <article className="system-health-card"><div><strong>{title}</strong>
    <span className={`state-badge ${healthy ? 'healthy' : 'offline'}`}>{healthy ? 'Healthy' : 'Degraded'}</span></div><p>{detail}</p></article>;
}
