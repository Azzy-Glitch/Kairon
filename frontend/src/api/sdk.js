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

/** Automatic signals for one paired app: { autoQueueDepth, autoRetries, retryWindowSeconds }. */
export function getAutoSignals(projectId, credentialId) {
  return request(client.get(`/v1/projects/${projectId}/credentials/${credentialId}/settings`));
}

export function updateAutoSignals(projectId, credentialId, settings) {
  return request(client.put(`/v1/projects/${projectId}/credentials/${credentialId}/settings`, settings));
}

export function revokeCredential(projectId, credentialId) {
  return request(client.delete(`/v1/projects/${projectId}/credentials/${credentialId}`));
}

/** sdkType is 'dotnet' or 'python'. For a re-pair, replacesCredentialId must be the exact
 * credential this session is meant to replace - the backend binds the session to it at creation
 * and later refuses to complete against any other credential (never omit it for a re-pair; a
 * session created without it can never be completed later). Omit entirely for a first-time
 * pairing session, which has no credential to replace. Optional `defaults` = { environment, service } become the SDK's defaults after redemption.
 * Returns { pairingId, code, expiresAt, sdkType, environment, service }. */
export function createPairing(projectId, sdkType, replacesCredentialId, defaults = {}) {
  const body = { sdkType, replacesCredentialId };
  // Optional defaults the SDK adopts once it redeems the code. Only sent when actually chosen, so
  // an omitted value keeps the backend's own default rather than sending an empty string.
  if (defaults.environment) body.environment = defaults.environment;
  if (defaults.service && defaults.service.trim()) body.service = defaults.service.trim();
  return request(client.post(`/v1/projects/${projectId}/pairing`, body));
}

/** Monitored applications (optionally for one project) with their logical `service` and
 * `lastTelemetryAt` - used to suggest service names. Never carries a credential. */
export function listApplications(projectId) {
  return request(client.get('/v1/platform/applications', { params: projectId ? { projectId } : {} }));
}

export function revokePairing(pairingId) {
  return request(client.delete(`/v1/platform/pairing/${pairingId}`));
}

/** Polled while a pairing code (including a re-pair code) is outstanding, and also used to
 * recover a re-pair's completion status after a refresh/navigation/lost response (SdkPage.jsx's
 * RecoveringCompletion effect). Returns
 * { pairingId, projectId, sdkType, environment, service, createdAt, expiresAt, redeemedAt, confirmedAt,
 *   completedAt, revokedAt, status, connection }
 * where connection (null until redeemed) is { lastTelemetryAt, application, service, environment,
 * source, machineHostName, machineConfirmedAt }
 * where status is 'Pending' | 'Redeemed' | 'Confirmed' | 'Completed' | 'Expired' | 'Cancelled'.
 * redeemedAt means the backend issued a fresh credential; confirmedAt (set later, by the SDK
 * itself) is the only trustworthy proof that the application actually received and is using it -
 * re-pairing must wait for confirmedAt, not redeemedAt, before it is safe to revoke the credential
 * being replaced. completedAt is the ONE authoritative signal that this session's own
 * complete-repair call actually finished (the old credential was revoked and targets rebound) -
 * the only thing that safely distinguishes "the completion request succeeded but its response was
 * lost" from "it never actually completed" after a refresh, without re-calling completeRepair to
 * find out. Never includes the code, its hash, or any credential secret. */
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
