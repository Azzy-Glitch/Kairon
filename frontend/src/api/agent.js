import client, { request } from './client';

/** Machine/process discovery - "Basic Monitoring" (docs/DESKTOP_SHELL.md): what the KAIRON
 * Agent has found running, with no SDK involved. */

export function getMachines() {
  return request(client.get('/agent/machines'));
}

export function getApplications() {
  return request(client.get('/agent/applications'));
}
