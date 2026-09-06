import client, { request } from './client';

/**
 * Read-only telemetry API for the operator UI. Telemetry ingestion belongs to instrumented
 * applications and SDKs; exposing write helpers here previously allowed synthetic samples to be
 * mixed into a real project's live readings.
 */

export function getTelemetryIncidents(projectId, service) {
  return request(client.get('/telemetry/incidents', { params: { projectId, service } }));
}

export function getMetrics(projectId, service) {
  const params = {};
  if (projectId) params.projectId = projectId;
  if (service) params.service = service;
  return request(client.get('/telemetry/metrics', { params }));
}
