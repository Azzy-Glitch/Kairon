import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Applications from './Applications';
import { getApplications, getMachines } from '../api/agent';
import { platformApi } from '../api/index';

vi.mock('../api/agent', () => ({ getApplications: vi.fn(), getMachines: vi.fn() }));
vi.mock('../api/index', () => ({ platformApi: {
  getPlatformApplications: vi.fn(), registerDiscoveredApplication: vi.fn(), createPairing: vi.fn(),
  getSdkInstallations: vi.fn(), revokeSdkInstallation: vi.fn()
} }));

describe('Applications', () => {
  beforeEach(() => { vi.clearAllMocks(); platformApi.getPlatformApplications.mockResolvedValue([]); });

  it('shows Agent monitoring as useful basic monitoring without an SDK', async () => {
    getMachines.mockResolvedValue([{ id: 'machine', status: 'Online' }]);
    getApplications.mockResolvedValue([{ id: 'app', machineName: 'WORKSTATION', processId: 42,
      name: 'orders', executable: 'orders.exe', runtime: '.NET', cpuPercent: 5,
      memoryBytes: 1048576, isRunning: true, monitoringLevel: 'Basic', telemetryStatus: 'Agent telemetry' }]);

    render(<Applications />);

    expect((await screen.findAllByText('orders')).length).toBeGreaterThan(0);
    expect(screen.getByText('Basic Monitoring')).toBeInTheDocument();
    expect(screen.getByText('Agent telemetry')).toBeInTheDocument();
    expect(screen.getByText('SDK not connected')).toBeInTheDocument();
  });

  it('registers a discovered application and generates a scoped pairing code', async () => {
    getMachines.mockResolvedValue([{ id: 'machine', status: 'Online' }]);
    getApplications.mockResolvedValue([{ id: 'discovered', machineName: 'WORKSTATION', processId: 42,
      name: 'orders', runtime: 'Python', cpuPercent: 5, memoryBytes: 1048576, isRunning: true,
      monitoringLevel: 'Basic', telemetryStatus: 'Agent telemetry' }]);
    platformApi.registerDiscoveredApplication.mockResolvedValue({ id: 'managed' });
    platformApi.createPairing.mockResolvedValue({ code: 'pair_temporary', expiresAt: '2026-08-28T12:00:00Z' });

    render(<Applications />);
    fireEvent.click(await screen.findByText('Generate pairing code'));

    expect(await screen.findByText('pair_temporary')).toBeInTheDocument();
    expect(platformApi.registerDiscoveredApplication).toHaveBeenCalledWith('discovered');
    expect(platformApi.createPairing).toHaveBeenCalledWith('managed', 'python');
    expect(screen.getByText(/scoped to this application/)).toBeInTheDocument();
  });
});
