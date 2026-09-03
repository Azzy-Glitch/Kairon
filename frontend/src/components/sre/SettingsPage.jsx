import React from 'react';
import { useHealth } from '../../hooks/useDemo';
import { useTheme } from '../../lib/ThemeContext';
import { POLLING_PRESETS, usePollingPreference } from '../../lib/PollingPreferenceContext';
import Tabs from '../ui/Tabs';
import { IconShield } from '../Icons';

const THEME_OPTIONS = [
  { id: 'system', label: 'System' },
  { id: 'light', label: 'Light' },
  { id: 'dark', label: 'Dark' }
];

/**
 * Settings (frontend PRD section 41): status only, never credentials.
 *
 * There is no configuration form here on purpose - the backend owns configuration (appsettings,
 * environment variables), and a frontend that could edit it would be a second, competing source of
 * truth. This page answers "what is Kairon currently configured to do", nothing more.
 */
export default function SettingsPage() {
  const { health, isLoading } = useHealth();
  const { theme, setTheme } = useTheme();
  const { multiplier, setMultiplier } = usePollingPreference();

  const activePreset = POLLING_PRESETS.find((p) => p.multiplier === multiplier) || POLLING_PRESETS[1];

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconShield className="w-6 h-6 tone-neutral" />
          </div>
          <div>
            <h3>Settings</h3>
            <p className="section-desc">Current platform configuration status. Configuration itself lives server-side.</p>
          </div>
        </div>
      </div>

      <section className="settings-preferences">
        <div className="settings-preference-row">
          <div>
            <span className="settings-row-label">Theme</span>
            <p className="panel-pending-text">
              System follows your OS setting; Light and Dark always override it.
            </p>
          </div>
          <Tabs
            items={THEME_OPTIONS}
            activeId={theme}
            onChange={setTheme}
          />
        </div>

        <div className="settings-preference-row">
          <div>
            <span className="settings-row-label">Polling rate</span>
            <p className="panel-pending-text">{activePreset.description}</p>
          </div>
          <Tabs
            items={POLLING_PRESETS}
            activeId={activePreset.id}
            onChange={(id) => setMultiplier(POLLING_PRESETS.find((p) => p.id === id).multiplier)}
          />
        </div>
      </section>

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
