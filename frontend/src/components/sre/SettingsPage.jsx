import React from 'react';
import { useHealth } from '../../hooks/useDemo';
import { IconShield } from '../Icons';

/**
 * Settings (frontend PRD section 41): status only, never credentials.
 *
 * There is no configuration form here on purpose - the backend owns configuration (appsettings,
 * environment variables), and a frontend that could edit it would be a second, competing source of
 * truth. This page answers "what is Kairon currently configured to do", nothing more.
 */
export default function SettingsPage() {
  const { health, isLoading } = useHealth();

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconShield className="w-6 h-6 text-slate-500" />
          </div>
          <div>
            <h3>Settings</h3>
            <p className="section-desc">Current platform configuration status. Configuration itself lives server-side.</p>
          </div>
        </div>
      </div>

      {isLoading ? (
        <p className="panel-pending-text">Loading status...</p>
      ) : (
        <div className="settings-grid">
          <SettingsRow label="AI provider" value={health.aiMode && health.aiMode !== 'unknown' ? health.aiMode : 'unknown'} online={health.aiService} />
          <SettingsRow label="Detection" value={health.detectionEnabled ? 'Enabled' : 'Disabled'} online={health.detectionEnabled} />
          <SettingsRow label="Remediation" value={health.remediationEnabled ? 'Enabled' : 'Disabled'} online={health.remediationEnabled} />
          <SettingsRow label="Database" value={health.database ? 'Connected' : 'Unavailable'} online={health.database} />
          <SettingsRow label="Database provider" value={health.databaseProvider || 'unknown'} online={health.database} />
          {typeof health.databaseSizeBytes === 'number' && (
            <SettingsRow label="Database size" value={formatBytes(health.databaseSizeBytes)} online={health.database} />
          )}
          <SettingsRow label="Persistence maintenance" value={health.maintenanceStatus || 'NotRun'} online={health.maintenanceStatus === 'Healthy'} />
          <SettingsRow label="Backend" value={health.backend ? 'Reachable' : 'Unreachable'} online={health.backend} />
        </div>
      )}

      <p className="settings-note">
        API keys, connection strings and other secrets are never sent to or stored in the frontend.
      </p>
    </div>
  );
}

function formatBytes(bytes) {
  if (!bytes) return '0 KB';
  const mb = bytes / 1024 / 1024;
  return mb >= 1 ? `${mb.toFixed(1)} MB` : `${(bytes / 1024).toFixed(1)} KB`;
}

function SettingsRow({ label, value, online }) {
  return (
    <div className="settings-row">
      <span className="settings-row-label">{label}</span>
      <span className="settings-row-value">
        <span className={`status-dot ${online ? 'online' : 'offline'}`} />
        {value}
      </span>
    </div>
  );
}
