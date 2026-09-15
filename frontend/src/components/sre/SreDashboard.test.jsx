import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

import SreDashboard from './SreDashboard';
import { SourceFilterProvider } from '../../lib/SourceFilterContext';

/**
 * RB-007: the headline "Overall health" tile must not read "Healthy" merely because the AI
 * process is alive when its configured provider is actually unreachable (a real outage) - but an
 * AI provider that is simply not configured yet remains a normal, non-degraded state.
 */
let mockDashboardData;

vi.mock('../../hooks/useIncidents', () => ({
  useDashboard: () => ({ data: mockDashboardData, isLoading: false, isError: false, error: null }),
  useIncidents: () => ({ incidents: [], isLoading: false, isError: false, error: null, reload: () => {} }),
  useRecentActivity: () => ({ data: [], isLoading: false, isError: false, error: null })
}));

function renderDashboard() {
  return render(
    <SourceFilterProvider>
      <SreDashboard />
    </SourceFilterProvider>
  );
}

function baseHealth(overrides) {
  return {
    backend: true,
    database: true,
    aiService: true,
    aiMode: 'groq',
    aiProviderReachable: true,
    ...overrides
  };
}

describe('SreDashboard overall health tile', () => {
  it('reads Degraded when the AI process is alive but its provider is unreachable', async () => {
    mockDashboardData = {
      health: baseHealth({ aiProviderReachable: false }),
      severityDistribution: {},
      activeIncidents: 0,
      activeServiceCount: 0
    };
    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText('Degraded')).toBeInTheDocument();
    });
    expect(screen.getByText('A subsystem needs attention')).toBeInTheDocument();
  });

  it('still reads Healthy when the AI provider is simply not configured yet', async () => {
    mockDashboardData = {
      health: baseHealth({ aiMode: 'unconfigured' }),
      severityDistribution: {},
      activeIncidents: 0,
      activeServiceCount: 0
    };
    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText('Healthy')).toBeInTheDocument();
    });
    expect(screen.getByText('All systems go')).toBeInTheDocument();
  });

  it('reads Degraded when the AI process itself is offline', async () => {
    mockDashboardData = {
      health: baseHealth({ aiService: false }),
      severityDistribution: {},
      activeIncidents: 0,
      activeServiceCount: 0
    };
    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText('Degraded')).toBeInTheDocument();
    });
  });

  it('reads Healthy when every subsystem including a reachable AI provider is up', async () => {
    mockDashboardData = {
      health: baseHealth(),
      severityDistribution: {},
      activeIncidents: 0,
      activeServiceCount: 0
    };
    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText('Healthy')).toBeInTheDocument();
    });
  });
});
