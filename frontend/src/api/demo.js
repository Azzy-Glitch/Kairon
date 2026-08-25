import client, { request } from './client';

/** Demo scenario control (frontend PRD section 14). */

export function getDemoState() {
  return request(client.get('/demo/state'));
}

export function startSimulation() {
  return request(client.post('/demo/simulate/start'));
}

export function stopSimulation() {
  return request(client.post('/demo/simulate/stop'));
}

/**
 * Asks the backend to run a detection pass now instead of waiting for its sweep. Useful when
 * presenting, so the incident appears the moment the metrics justify it.
 */
export function forceEvaluate() {
  return request(client.post('/demo/evaluate'));
}
