import client, { request } from './client';

/**
 * The original Kairon analysis endpoints: Error Analyzer, API Drift Guard, Risk Radar, Arch
 * Advisor, Audit History and Statistics.
 *
 * Every route here is unchanged (frontend PRD section 2 and 22). The service layer exists so the
 * screens share one client, not to alter what they call.
 */

export function analyzeError(log) {
  return request(client.post('/analyze-error', { log }));
}

export function validateApi(expected, actual) {
  return request(client.post('/validate-api', { expected, actual }));
}

export function predict(recentLogs, currentLog) {
  return request(client.post('/predict', { recent_logs: recentLogs, current_log: currentLog }));
}

export function recommend(context) {
  return request(client.post('/recommend', { context }));
}

export function getHistory(limit = 25) {
  return request(client.get('/history', { params: { limit } }));
}

export function clearHistory() {
  return request(client.delete('/history'));
}

export function getStats() {
  return request(client.get('/stats'));
}
