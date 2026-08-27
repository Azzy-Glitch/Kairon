import React from 'react';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Applications from './Applications';
import { getApplications, getMachines } from '../api/agent';

vi.mock('../api/agent', () => ({ getApplications: vi.fn(), getMachines: vi.fn() }));

describe('Applications', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows Agent monitoring as useful basic monitoring without an SDK', async () => {
    getMachines.mockResolvedValue([{ id: 'machine', status: 'Online' }]);
    getApplications.mockResolvedValue([{ id: 'app', machineName: 'WORKSTATION', processId: 42,
      name: 'orders', executable: 'orders.exe', runtime: '.NET', cpuPercent: 5,
      memoryBytes: 1048576, isRunning: true, monitoringLevel: 'Basic', telemetryStatus: 'Agent telemetry' }]);

    render(<Applications />);

    expect(await screen.findByText('orders')).toBeInTheDocument();
    expect(screen.getByText('Basic Monitoring')).toBeInTheDocument();
    expect(screen.getByText('Agent telemetry')).toBeInTheDocument();
  });
});
