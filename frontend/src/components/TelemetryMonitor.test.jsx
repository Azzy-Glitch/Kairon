import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import TelemetryMonitor from './TelemetryMonitor';

vi.mock('../api/index', () => ({
  sdkApi: {
    listProjects: vi.fn()
  },
  telemetryApi: {
    getTelemetryIncidents: vi.fn(),
    getMetrics: vi.fn()
  }
}));

vi.mock('./ui/MetricTile', () => ({
  default: ({ label, value, unit }) => <div>{label}: {value ?? 'No data'}{value == null ? '' : unit}</div>
}));

describe('TelemetryMonitor', () => {
  let sdkApi;
  let telemetryApi;

  beforeEach(async () => {
    ({ sdkApi, telemetryApi } = await import('../api/index'));
    sdkApi.listProjects.mockReset().mockResolvedValue([
      { id: '18093e69-0faa-471b-ae6d-cda6164e338e', name: 'Payment API' }
    ]);
    telemetryApi.getTelemetryIncidents.mockReset().mockResolvedValue([]);
    telemetryApi.getMetrics.mockReset().mockResolvedValue([]);
  });

  it('is read-only and loads telemetry without offering synthetic data seeding', async () => {
    const user = userEvent.setup();
    render(<TelemetryMonitor />);

    const pythonProject = await screen.findByLabelText('Project telemetry project');
    await user.selectOptions(pythonProject, '18093e69-0faa-471b-ae6d-cda6164e338e');

    await waitFor(() => {
      expect(telemetryApi.getTelemetryIncidents).toHaveBeenCalledWith('18093e69-0faa-471b-ae6d-cda6164e338e', undefined);
      expect(telemetryApi.getMetrics).toHaveBeenCalledWith('18093e69-0faa-471b-ae6d-cda6164e338e', undefined);
    });
    expect(screen.queryByRole('button', { name: /seed test data/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Python project')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Service filter'), 'OrderApi');
    await user.click(screen.getByRole('button', { name: 'Apply filter and refresh' }));
    await waitFor(() => {
      expect(telemetryApi.getTelemetryIncidents).toHaveBeenLastCalledWith('18093e69-0faa-471b-ae6d-cda6164e338e', 'OrderApi');
      expect(telemetryApi.getMetrics).toHaveBeenLastCalledWith('18093e69-0faa-471b-ae6d-cda6164e338e', 'OrderApi');
    });
  });
});
