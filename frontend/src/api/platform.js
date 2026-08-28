import client, { request } from './client';

export function getPlatformApplications(projectId) {
  return request(client.get('/v1/platform/applications', { params: projectId ? { projectId } : {} }));
}
export function registerDiscoveredApplication(id) { return request(client.post(`/v1/platform/applications/from-discovery/${id}`)); }
export function createPairing(id, sdkType) { return request(client.post(`/v1/platform/applications/${id}/pairing`, { sdkType })); }
export function getSdkInstallations(applicationId) { return request(client.get('/v1/platform/sdk-installations', { params: { applicationId } })); }
export function revokeSdkInstallation(id) { return request(client.delete(`/v1/platform/sdk-installations/${id}`)); }
