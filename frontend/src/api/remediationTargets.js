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

/** Preflight-only - validates without persisting. Returns { valid, errors }. */
export function validate(payload) {
  return request(client.post('/v1/remediation-targets/validate', payload));
}
