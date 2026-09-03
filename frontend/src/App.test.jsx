import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import App from './App';

/**
 * Navigation smoke test (frontend PRD section 46).
 *
 * The api layer is mocked at the module boundary rather than components being coupled to axios
 * directly - the same boundary every hook already calls through, so this exercises real navigation
 * and real data-fetching wiring without hitting a real backend.
 */
vi.mock('./api/index', () => ({
  incidentsApi: {
    listIncidents: vi.fn().mockResolvedValue([]),
    getIncident: vi.fn().mockResolvedValue(null),
    getTimeline: vi.fn().mockResolvedValue([]),
    getEvidence: vi.fn().mockResolvedValue(null),
    getDashboard: vi.fn().mockResolvedValue({ severityDistribution: {} }),
    getRecentActivity: vi.fn().mockResolvedValue([]),
    getTools: vi.fn().mockResolvedValue([]),
    investigate: vi.fn().mockResolvedValue(null),
    approveAction: vi.fn().mockResolvedValue(null),
    rejectAction: vi.fn().mockResolvedValue(null),
    cancelIncident: vi.fn().mockResolvedValue(null)
  },
  telemetryApi: {
    getTelemetryIncidents: vi.fn().mockResolvedValue([]),
    getMetrics: vi.fn().mockResolvedValue([]),
    postTelemetryIncident: vi.fn().mockResolvedValue(null),
    postMetric: vi.fn().mockResolvedValue(null)
  },
  devopsApi: {
    analyzeError: vi.fn().mockResolvedValue({}),
    validateApi: vi.fn().mockResolvedValue({}),
    predict: vi.fn().mockResolvedValue({}),
    recommend: vi.fn().mockResolvedValue({}),
    getHistory: vi.fn().mockResolvedValue([]),
    clearHistory: vi.fn().mockResolvedValue(null),
    getStats: vi.fn().mockResolvedValue({})
  },
  demoApi: {
    getDemoState: vi.fn().mockResolvedValue({ running: false }),
    startSimulation: vi.fn().mockResolvedValue(null),
    stopSimulation: vi.fn().mockResolvedValue(null),
    forceEvaluate: vi.fn().mockResolvedValue(null)
  },
  healthApi: {
    getHealthStatus: vi.fn().mockResolvedValue({
      backend: true,
      database: true,
      aiService: true,
      detectionEnabled: true,
      remediationEnabled: true,
      aiMode: 'mock'
    })
  },
  sdkApi: {
    listProjects: vi.fn().mockResolvedValue([]),
    createProject: vi.fn().mockResolvedValue({ id: 'p1', name: 'New Project' }),
    listCredentials: vi.fn().mockResolvedValue([]),
    revokeCredential: vi.fn().mockResolvedValue(null),
    createPairing: vi.fn().mockResolvedValue({ pairingId: 'pair1', code: 'pair_x', expiresAt: new Date().toISOString(), sdkType: 'dotnet' }),
    revokePairing: vi.fn().mockResolvedValue(null)
  },
  agentApi: {
    getMachines: vi.fn().mockResolvedValue([]),
    getApplications: vi.fn().mockResolvedValue([])
  }
}));

// Approved navigation: 12 destinations under four sentence-case groups (Monitor, Respond,
// Analyse, Build), all always visible - no collapsed/secondary group in this structure.
const NAV_TABS = [
  'Overview',
  'Services',
  'Live telemetry',
  'Machines',
  'Incidents',
  'Actions',
  'Audit trail',
  'Insights',
  'Analytics',
  'Connect an app',
  'Diagnostics',
  'Demo',
  'Settings'
];

describe('App navigation', () => {
  it('shows Kairon branding, not the old AIDIP name', () => {
    render(<App />);
    expect(screen.getByText('Kairon')).toBeInTheDocument();
    expect(screen.queryByText(/AIDIP/i)).not.toBeInTheDocument();
  });

  it('renders every nav tab, grouped under Monitor/Respond/Analyse/Build', () => {
    render(<App />);
    for (const group of ['Monitor', 'Respond', 'Analyse', 'Build']) {
      expect(screen.getByText(group)).toBeInTheDocument();
    }
    for (const label of NAV_TABS) {
      expect(screen.getByRole('button', { name: new RegExp(`^${label}`) })).toBeInTheDocument();
    }
  });

  it('lands on Overview (the SRE command center) by default', () => {
    render(<App />);
    const overviewTab = screen.getByRole('button', { name: /^Overview/ });
    expect(overviewTab.className).toMatch(/active/);
  });

  it('reflects real incident severity in the header health pill, not just component health', async () => {
    // App.jsx and SreDashboard (rendered by default on the Overview tab) each call useDashboard()
    // independently - mockResolvedValue (not Once) so both call sites see the same data.
    const { incidentsApi } = await import('./api/index');
    incidentsApi.getDashboard.mockResolvedValue({ severityDistribution: { Critical: 2 } });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/Health: Critical/)).toBeInTheDocument();
    });
  });

  it.each(NAV_TABS)('switches to %s without throwing', async (label) => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(screen.getByRole('button', { name: new RegExp(`^${label}`) }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: new RegExp(`^${label}`) }).className).toMatch(/active/);
    });
  });
});
