import React, { useEffect, useState } from 'react';
import { useHealth } from '../../hooks/useHealth';
import { useTheme } from '../../lib/ThemeContext';
import { POLLING_PRESETS, usePollingPreference } from '../../lib/PollingPreferenceContext';
import Tabs from '../ui/Tabs';
import Button from '../ui/Button';
import Badge from '../ui/Badge';
import { useToast } from '../Toast';
import { aiConfigApi, dataManagementApi } from '../../api';
import { IconDownload, IconShield, IconRefresh, IconTrash } from '../Icons';

const THEME_OPTIONS = [
  { id: 'system', label: 'System' },
  { id: 'light', label: 'Light' },
  { id: 'dark', label: 'Dark' }
];

/**
 * Settings (frontend PRD section 41): status only, never credentials - with one deliberate
 * exception, the AI Configuration panel below.
 *
 * Every other row here is a read-only reflection of config Kairon already owns end-to-end
 * (appsettings.json toggles, environment variables) - a form that could edit those would be a
 * second, competing source of truth. AI provider/model/key is different: before this panel
 * existed, configuring it required hand-editing ai-service/.env and backend/appsettings.json,
 * which is exactly the friction this product should not ask an end user to have. The panel is the
 * ONE source of truth for that specific setting from now on (persisted server-side, encrypted at
 * rest) - it does not duplicate or shadow anything else on this page.
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

      <AiConfigurationSection />

      <DataManagementSection databaseAvailable={health.database} />

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
        API keys are written once, applied server-side and never sent back to or stored in the
        frontend - not in this page's own state after a save, not in localStorage, not in telemetry.
      </p>
    </div>
  );
}

const PROVIDERS = [
  { id: 'groq', label: 'Groq' },
  { id: 'qwen', label: 'Qwen' },
  { id: 'gemini', label: 'Gemini' }
];

const AUTO_MODEL = '__auto__';
const CUSTOM_MODEL = '__custom__';

/**
 * A test that fell back to the built-in mock is NOT a working provider connection. The AI service
 * reports this honestly as effective_provider="mock" (it answers successfully, just not from the
 * provider), so surfacing it as a plain green success is what would mislead - a user would believe
 * their real key works when no provider was ever reached.
 */
const isMockResult = (result) => (result?.effectiveProvider || '').toLowerCase() === 'mock';

/**
 * "Install KAIRON -> open KAIRON -> choose a provider -> paste a key -> optionally choose a
 * model -> Test Connection -> done" (frontend PRD section 15) - no .env, no appsettings.json, no
 * manual restart. Talks only to the existing backend API (api/v1/ai-config); the browser never
 * talks to Groq/Qwen/Gemini directly and the stored key is never returned to it.
 */
function AiConfigurationSection() {
  const toast = useToast();

  const [loaded, setLoaded] = useState(false);
  const [saved, setSaved] = useState({ provider: '', model: '', endpoint: '', hasApiKey: false, updatedAt: null });

  const [provider, setProvider] = useState('groq');
  const [apiKey, setApiKey] = useState('');
  const [modelChoice, setModelChoice] = useState(AUTO_MODEL);
  const [customModel, setCustomModel] = useState('');
  const [discoveredModels, setDiscoveredModels] = useState([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [endpoint, setEndpoint] = useState('');

  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState(null);

  useEffect(() => {
    let cancelled = false;
    aiConfigApi
      .getConfig()
      .then((config) => {
        if (cancelled) return;
        setSaved(config);
        if (config.provider) setProvider(config.provider);
        if (config.model) {
          setModelChoice(CUSTOM_MODEL);
          setCustomModel(config.model);
        }
        if (config.endpoint) setEndpoint(config.endpoint);
      })
      .catch(() => {
        // No saved configuration yet, or the backend isn't reachable - the form still works, it
        // just starts from the Groq/Auto defaults rather than a previous selection.
      })
      .finally(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const changeProvider = (nextProvider) => {
    setProvider(nextProvider);
    setApiKey('');
    setModelChoice(AUTO_MODEL);
    setCustomModel('');
    setDiscoveredModels([]);
    setEndpoint('');
    setTestResult(null);
  };

  const resolvedModel = () => {
    if (modelChoice === AUTO_MODEL) return '';
    if (modelChoice === CUSTOM_MODEL) return customModel.trim();
    return modelChoice;
  };

  const refreshModels = async () => {
    setLoadingModels(true);
    try {
      const result = await aiConfigApi.listModels({ provider, apiKey: apiKey.trim() || undefined });
      if (result.supported) {
        setDiscoveredModels(result.models || []);
        if ((result.models || []).length === 0) {
          toast.addToast(result.error || 'No models found - check the API key.', 'info');
        }
      } else {
        toast.addToast('This provider does not support live model discovery - enter a model name manually.', 'info');
      }
    } catch (err) {
      toast.addToast(err?.message || 'Could not load models', 'error');
    } finally {
      setLoadingModels(false);
    }
  };

  const handleTest = async () => {
    setTesting(true);
    setTestResult(null);
    try {
      const result = await aiConfigApi.testConnection({
        provider,
        apiKey: apiKey.trim() || undefined,
        model: resolvedModel(),
        endpoint: endpoint.trim()
      });
      setTestResult(result);
      toast.addToast(
        !result.success
          ? `Connection failed: ${result.error || 'unknown error'}`
          : isMockResult(result)
            ? 'Not connected - Kairon used its built-in mock, not your provider. Check the API key.'
            : 'Connection successful',
        !result.success ? 'error' : isMockResult(result) ? 'info' : 'success'
      );
    } catch (err) {
      setTestResult({ success: false, error: err?.message || 'Could not reach the backend.' });
      toast.addToast(err?.message || 'Test failed', 'error');
    } finally {
      setTesting(false);
    }
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      const result = await aiConfigApi.saveConfig({
        provider,
        apiKey: apiKey.trim() || undefined,
        model: resolvedModel(),
        endpoint: endpoint.trim()
      });
      setSaved(result);
      setApiKey('');
      toast.addToast(
        result.applied === false
          ? 'Saved. The AI service is still starting - it will apply automatically once it is ready.'
          : 'AI configuration saved',
        result.applied === false ? 'info' : 'success'
      );
    } catch (err) {
      toast.addToast(err?.message || 'Could not save AI configuration', 'error');
    } finally {
      setSaving(false);
    }
  };

  const providerChanged = saved.provider && saved.provider !== provider;

  return (
    <section className="settings-ai-config">
      <div className="settings-ai-config-head">
        <span className="settings-row-label">AI configuration</span>
        <p className="panel-pending-text">
          Choose a provider, paste an API key, optionally pick a model, then test and save.
        </p>
      </div>

      <div className="settings-ai-config-grid">
        <label className="block-label" htmlFor="ai-config-provider">
          Provider
        </label>
        <select
          id="ai-config-provider"
          className="approval-input settings-ai-config-select"
          value={provider}
          onChange={(e) => changeProvider(e.target.value)}
        >
          {PROVIDERS.map((p) => (
            <option key={p.id} value={p.id}>
              {p.label}
            </option>
          ))}
        </select>

        <label className="block-label" htmlFor="ai-config-key">
          API key
        </label>
        <input
          id="ai-config-key"
          type="password"
          className="approval-input"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={
            saved.hasApiKey && !providerChanged
              ? 'Saved - leave blank to keep the current key'
              : `Paste your ${PROVIDERS.find((p) => p.id === provider)?.label} API key`
          }
          autoComplete="off"
        />

        <label className="block-label" htmlFor="ai-config-model">
          Model
        </label>
        <div className="settings-ai-config-model-row">
          <select
            id="ai-config-model"
            className="approval-input settings-ai-config-select"
            value={modelChoice}
            onChange={(e) => setModelChoice(e.target.value)}
          >
            <option value={AUTO_MODEL}>Auto / Recommended</option>
            {discoveredModels.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
            <option value={CUSTOM_MODEL}>Custom model...</option>
          </select>
          {provider === 'groq' && (
            <Button
              variant="ghost"
              size="compact"
              onClick={refreshModels}
              disabled={loadingModels}
              title="Refresh available models"
              aria-label="Refresh available models"
            >
              <IconRefresh className={`w-4 h-4 ${loadingModels ? 'animate-spin' : ''}`} />
            </Button>
          )}
        </div>
        {modelChoice === CUSTOM_MODEL && (
          <input
            className="approval-input settings-ai-config-custom-model"
            value={customModel}
            onChange={(e) => setCustomModel(e.target.value)}
            placeholder="model-name"
            autoComplete="off"
          />
        )}

        <label className="block-label" htmlFor="ai-config-endpoint">
          Custom endpoint
        </label>
        <input
          id="ai-config-endpoint"
          className="approval-input"
          value={endpoint}
          onChange={(e) => setEndpoint(e.target.value)}
          placeholder="Leave blank to use the provider's default endpoint"
          autoComplete="off"
        />
        <p className="settings-ai-config-endpoint-hint panel-pending-text">
          Only needed if this provider is fronted by a dedicated or regional URL instead of its
          shared public one - for example an Alibaba Model Studio Token Plan workspace.
        </p>
      </div>

      <div className="settings-ai-config-actions">
        <Button variant="secondary" onClick={handleTest} disabled={testing || saving}>
          {testing ? 'Testing...' : 'Test Connection'}
        </Button>
        <Button variant="primary" onClick={handleSave} disabled={saving || testing}>
          {saving ? 'Saving...' : 'Save Configuration'}
        </Button>
        {loaded && saved.hasApiKey && (
          <span className="settings-ai-config-saved-note">
            Currently configured: {saved.provider}{saved.model ? ` · ${saved.model}` : ' · Auto'}
            {saved.endpoint ? ` · ${saved.endpoint}` : ''}
          </span>
        )}
      </div>

      {testResult && (
        <div className="settings-ai-config-result">
          <Badge tone={testResult.success ? (isMockResult(testResult) ? 'medium' : 'healthy') : 'critical'}>
            {!testResult.success
              ? 'Connection failed'
              : isMockResult(testResult)
                ? 'Not connected - using mock responses'
                : 'Connection successful'}
          </Badge>
          {testResult.success ? (
            <span className="panel-pending-text">
              {isMockResult(testResult)
                ? 'No usable API key for this provider, so Kairon answered from its built-in mock instead of reaching the provider. Add a valid key and test again.'
                : `Provider: ${testResult.effectiveProvider || testResult.provider}${testResult.model ? ` · Model: ${testResult.model}` : ''}`}
            </span>
          ) : (
            <span className="panel-pending-text">{testResult.error || 'Unknown error.'}</span>
          )}
        </div>
      )}
    </section>
  );
}

function DataManagementSection({ databaseAvailable }) {
  const toast = useToast();
  const [downloading, setDownloading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [confirmation, setConfirmation] = useState('');
  const [deletedResult, setDeletedResult] = useState(null);

  const download = async () => {
    setDownloading(true);
    try {
      const result = await dataManagementApi.downloadData();
      const url = URL.createObjectURL(result.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = result.fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      toast.addToast('Kairon database downloaded', 'success');
    } catch (error) {
      toast.addToast(error?.message || 'Could not download the database', 'error');
    } finally {
      setDownloading(false);
    }
  };

  const deleteAll = async () => {
    if (confirmation !== 'DELETE') return;
    setDeleting(true);
    try {
      const result = await dataManagementApi.deleteAllData(confirmation);
      setDeletedResult(result);
      setConfirmation('');
      setConfirming(false);
      toast.addToast('All Kairon database data deleted', 'success');
    } catch (error) {
      toast.addToast(error?.message || 'Could not delete the database data', 'error');
    } finally {
      setDeleting(false);
    }
  };

  return (
    <section className="settings-data-management">
      <div className="settings-data-management-head">
        <span className="settings-row-label">Your data</span>
        <p className="panel-pending-text">
          Download a portable SQLite copy or permanently remove all records stored by KAIRON.
        </p>
      </div>

      <div className="settings-data-management-actions">
        <Button variant="secondary" onClick={download} disabled={!databaseAvailable || downloading || deleting}>
          <IconDownload className="w-4 h-4 mr-1" />
          {downloading ? 'Preparing download...' : 'Download my data'}
        </Button>
        <Button variant="danger" onClick={() => setConfirming(true)} disabled={!databaseAvailable || downloading || deleting || confirming}>
          <IconTrash className="w-4 h-4 mr-1" />
          Delete all data
        </Button>
      </div>

      {confirming && (
        <div className="settings-delete-confirmation" role="group" aria-label="Confirm data deletion">
          <div>
            <strong>This cannot be undone.</strong>
            <p className="panel-pending-text">
              Projects, telemetry, incidents, AI configuration, credentials, audit history and KAIRON-created database backups will be removed.
              Download your data first if you want to keep a copy.
            </p>
          </div>
          <label className="block-label" htmlFor="delete-data-confirmation">Type DELETE to confirm</label>
          <input
            id="delete-data-confirmation"
            className="approval-input"
            value={confirmation}
            onChange={(event) => setConfirmation(event.target.value)}
            autoComplete="off"
          />
          <div className="settings-delete-confirmation-actions">
            <Button variant="ghost" onClick={() => { setConfirming(false); setConfirmation(''); }} disabled={deleting}>
              Cancel
            </Button>
            <Button variant="danger" onClick={deleteAll} disabled={confirmation !== 'DELETE' || deleting}>
              {deleting ? 'Deleting...' : 'Permanently delete data'}
            </Button>
          </div>
        </div>
      )}

      {deletedResult && (
        <p className="settings-data-deleted" role="status">
          Deleted {deletedResult.deletedRecords} database records and {deletedResult.deletedBackups} stored backups.
          Restart KAIRON to begin with a completely fresh session.
        </p>
      )}
    </section>
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
