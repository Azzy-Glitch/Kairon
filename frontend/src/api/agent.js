import client, { request } from './client';

export function getMachines() { return request(client.get('/agent/machines')); }
export function getApplications() { return request(client.get('/agent/applications')); }
