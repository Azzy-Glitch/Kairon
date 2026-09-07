import client, { request } from './client';
export const getConfig = () => request(client.get('/v1/database-config'));
export const testConnection = (settings) => request(client.post('/v1/database-config/test', settings));
export const saveConfig = (settings) => request(client.post('/v1/database-config', settings));
