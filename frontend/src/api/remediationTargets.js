import client, { request } from './client';

/**
 * The operator-authorized remediation-target management API (backend/Controllers/
 * RemediationTargetsController.cs). Configuration only - never executes anything, never approves
 * or creates a remediation action. Cross-project, top-level collection (like /api/incidents and
 * /api/telemetry), filtered by optional query params rather than nested under a project route.
 */

export function list(filters = {}) {
  return request(client.get('/v1/remediation-targets', { params: filters }));
}

export function get(id) {
  return request(client.get(`/v1/remediation-targets/${id}`));
}

export function create(payload) {
  return request(client.post('/v1/remediation-targets', payload));
}

/** payload may include expectedUpdatedAt for optimistic-concurrency checking; a stale value
 * produces a 409 with errorCode "stale-update" (see toOperatorError in ./client). */
export function update(id, payload) {
  return request(client.put(`/v1/remediation-targets/${id}`, payload));
}

/** Soft-delete: disables the target. The row is retained and can be re-enabled. */
export function disable(id) {
  return request(client.delete(`/v1/remediation-targets/${id}`));
}

export function enable(id) {
  return request(client.post(`/v1/remediation-targets/${id}/enable`));
}

/** One click from Connect an App: let KAIRON restart this connected application when an operator
 * approves a fix. The backend derives everything from the pairing session and the Agent-confirmed
 * machine - nothing is chosen in the browser. Idempotent. */
export function enableAppRestartFromPairing(pairingId) {
  return request(client.post(`/v1/remediation-targets/app-process/from-pairing/${pairingId}`));
}

/** Preflight-only - validates without persisting. Returns { valid, errors }. */
export function validate(payload) {
  return request(client.post('/v1/remediation-targets/validate', payload));
}

/** Read-only Windows pre-flight for a prospective target (same body as create). Persists nothing,
 * never changes a service or its permissions. Returns
 * { readiness, canEnable, checks: [{ key, label, passed, blocking, detail, readiness }], service,
 *   requiredRights, missingRights, executorAccount, executorSid, fixCommand }. */
export function preflight(payload) {
  return request(client.post('/v1/remediation-targets/preflight', payload));
}

/** The same pre-flight for a saved target - adds the service-identity and Agent-proof checks. */
export function targetPreflight(id) {
  return request(client.get(`/v1/remediation-targets/${id}/preflight`));
}

/** Windows services on an enrolled machine, with eligibility. Only the KAIRON host itself can be
 * queried: any other machine is a 422 with code "remote-not-supported"; an unknown machine is 404. */
export function machineServices(machineId) {
  return request(client.get(`/v1/remediation-targets/machines/${machineId}/services`));
}
