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

/** sdkType is 'dotnet' or 'python'. For a re-pair, replacesCredentialId must be the exact
 * credential this session is meant to replace - the backend binds the session to it at creation
 * and later refuses to complete against any other credential (never omit it for a re-pair; a
 * session created without it can never be completed later). Omit entirely for a first-time
 * pairing session, which has no credential to replace. Returns { pairingId, code, expiresAt, sdkType }. */
export function createPairing(projectId, sdkType, replacesCredentialId) {
  return request(client.post(`/v1/projects/${projectId}/pairing`, { sdkType, replacesCredentialId }));
}

export function revokePairing(pairingId) {
  return request(client.delete(`/v1/platform/pairing/${pairingId}`));
}

/** Polled while a pairing code (including a re-pair code) is outstanding. Returns
 * { pairingId, projectId, sdkType, createdAt, expiresAt, redeemedAt, confirmedAt, revokedAt, status }
 * where status is 'Pending' | 'Redeemed' | 'Expired' | 'Cancelled'. redeemedAt means the backend
 * issued a fresh credential; confirmedAt (set later, by the SDK itself) is the only trustworthy
 * proof that the application actually received and is using it - re-pairing must wait for
 * confirmedAt, not redeemedAt, before it is safe to revoke the credential being replaced. Never
 * includes the code, its hash, or any credential secret. */
export function getPairingStatus(pairingId) {
  return request(client.get(`/v1/platform/pairing/${pairingId}`));
}

/** Completes a re-pair: atomically rebinds every enabled remediation target bound to
 * oldCredentialId onto the newly issued credential, then revokes oldCredentialId. The backend
 * refuses (409) unless the pairing session has been confirmed by the application itself. Returns
 * { rebindCount }. */
export function completeRepair(pairingId, oldCredentialId) {
  return request(client.post(`/v1/platform/pairing/${pairingId}/complete-repair`, { oldCredentialId }));
}
