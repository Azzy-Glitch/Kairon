import client, { request } from './client';

/**
 * The AI Configuration panel's backend surface (Settings page): pick a provider, paste a key,
 * optionally pick a model, Test Connection, Save - never a .env or appsettings.json edit.
 *
 * `/v1/...` rather than `/api/...` - the same deliberately versioned platform-management
 * namespace sdk.js already uses, not the older `/api/telemetry`/`/api/incidents` routes.
 */

/** Never includes the key - only { provider, model, hasApiKey, updatedAt }. */
export function getConfig() {
  return request(client.get('/v1/ai-config'));
}

/** apiKey is optional - omit it to keep whatever key is already saved (e.g. when only the model
 * is changing). model is optional/blank for "Auto / Recommended". */
export function saveConfig({ provider, apiKey, model }) {
  return request(client.post('/v1/ai-config', { provider, apiKey, model }));
}

/** apiKey is optional - omit it to test the already-saved key for this provider. */
export function testConnection({ provider, apiKey, model }) {
  return request(client.post('/v1/ai-config/test', { provider, apiKey, model }));
}

/** Live model discovery where the provider supports it (Groq only today) - { supported: false }
 * for providers that don't, so the caller falls back to manual model entry. */
export function listModels({ provider, apiKey }) {
  return request(client.post('/v1/ai-config/models', { provider, apiKey }));
}
