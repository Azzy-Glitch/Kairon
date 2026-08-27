import client, { request } from './client';

/**
 * Raw telemetry API. These are the pre-existing endpoints the Telemetry Monitor screen has always
 * used; they are wrapped here so that screen stops calling axios directly, without any change to
 * the routes it depends on.
 */

export function getTelemetryIncidents(projectId) {
  return request(client.get('/telemetry/incidents', { params: projectId ? { projectId } : {} }));
}

export function getMetrics(projectId, service) {
  const params = {};
  if (projectId) params.projectId = projectId;
  if (service) params.service = service;
  return request(client.get('/telemetry/metrics', { params }));
}

export function postTelemetryIncident(payload) {
  return request(client.post('/telemetry/incidents', payload));
}

export function postMetric(payload) {
  return request(client.post('/telemetry/metrics', payload));
}
