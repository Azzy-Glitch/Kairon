import client, { request } from './client';

/** Liveness probe. The original screens have always used this route. */
export function getHealth() {
  return request(client.get('/health'));
}

/**
 * Component-level health: backend, database, AI service, and whether detection and remediation are
 * enabled. Lets the UI say which subsystem is down rather than just "offline"
 * (frontend PRD section 18).
 */
export function getHealthStatus() {
  return request(client.get('/health/status'));
}
