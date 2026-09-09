import client, { request } from './client';

/**
 * Projects, per-project credentials, and SDK pairing (docs/DESKTOP_SHELL.md).
 *
 * These routes live at /api/v1/... rather than /api/... - a deliberate versioned namespace for
 * the platform-management surface, separate from the existing /api/telemetry and /api/incidents
 * routes it sits alongside.
 */

export function listProjects() {
  return request(client.get('/v1/projects'));
}

export function createProject(name) {
  return request(client.post('/v1/projects', { name }));
}

export function listCredentials(projectId) {
  return request(client.get(`/v1/projects/${projectId}/credentials`));
}

export function revokeCredential(projectId, credentialId) {
  return request(client.delete(`/v1/projects/${projectId}/credentials/${credentialId}`));
}

/** sdkType is 'dotnet' or 'python'. Returns { pairingId, code, expiresAt, sdkType }. */
export function createPairing(projectId, sdkType) {
  return request(client.post(`/v1/projects/${projectId}/pairing`, { sdkType }));
}

export function revokePairing(pairingId) {
  return request(client.delete(`/v1/platform/pairing/${pairingId}`));
}

/** Polled while a pairing code (including a re-pair code) is outstanding. Returns
 * { pairingId, projectId, sdkType, createdAt, expiresAt, redeemedAt, revokedAt, status } where
 * status is 'Pending' | 'Redeemed' | 'Expired' | 'Cancelled'. Never includes the code or a secret. */
export function getPairingStatus(pairingId) {
  return request(client.get(`/v1/platform/pairing/${pairingId}`));
}
