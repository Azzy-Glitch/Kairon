import client, { request } from './client';

/**
 * Autonomous SRE incident API.
 *
 * Components never call axios directly; they call these (frontend PRD section 16), so a backend
 * route change is a one-file edit rather than a hunt through the component tree.
 */

export function listIncidents({ status, severity, service, limit = 50 } = {}) {
  const params = {};
  if (status) params.status = status;
  if (severity) params.severity = severity;
  if (service) params.service = service;
  if (limit) params.limit = limit;

  return request(client.get('/incidents', { params }));
}

export function getIncident(id) {
  return request(client.get(`/incidents/${id}`));
}

export function getTimeline(id) {
  return request(client.get(`/incidents/${id}/timeline`));
}

/** The evidence package the AI was given, so an operator can audit the diagnosis. */
export function getEvidence(id) {
  return request(client.get(`/incidents/${id}/evidence`));
}

export function getDashboard(projectId) {
  return request(client.get('/incidents/dashboard', { params: projectId ? { projectId } : {} }));
}

/** Most recent audit events across every incident, newest first - the Overview activity feed. */
export function getRecentActivity(limit = 20) {
  return request(client.get('/incidents/activity', { params: { limit } }));
}

/** Registered remediation tools and whether policy currently permits each one. */
export function getTools() {
  return request(client.get('/incidents/tools'));
}

/** Queues a fresh AI investigation. Returns immediately; the work happens in the background. */
export function investigate(id) {
  return request(client.post(`/incidents/${id}/investigate`));
}

/**
 * Approves one remediation action. This is the only call in the entire frontend that can cause
 * anything to execute, and it always requires an explicit operator identity.
 */
export function approveAction(incidentId, actionId, approvedBy, note) {
  return request(
    client.post(`/incidents/${incidentId}/actions/${actionId}/approve`, { approvedBy, note })
  );
}

export function rejectAction(incidentId, actionId, rejectedBy, reason) {
  return request(
    client.post(`/incidents/${incidentId}/actions/${actionId}/reject`, { rejectedBy, reason })
  );
}

export function cancelIncident(incidentId, cancelledBy, reason) {
  return request(client.post(`/incidents/${incidentId}/cancel`, { cancelledBy, reason }));
}
