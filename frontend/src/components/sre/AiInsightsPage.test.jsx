import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

import AiInsightsPage from './AiInsightsPage';
import { SourceFilterProvider } from '../../lib/SourceFilterContext';

function renderPage() {
  return render(
    <SourceFilterProvider>
      <AiInsightsPage />
    </SourceFilterProvider>
  );
}

/**
 * RB-007: the AI service status dot on this page must reflect actual provider
 * configuration/reachability (describeAiStatus's tone), not merely whether the AI process is
 * alive - a process that is up but has no configured/reachable provider must show as offline
 * here, not online.
 */
vi.mock('../../hooks/useIncidents', () => ({
  useIncidents: () => ({ incidents: [], isLoading: false, isError: false, error: null, reload: () => {} }),
  useIncidentDetails: () => ({ data: [], isLoading: false, isError: false, error: null })
}));

let mockHealth;
vi.mock('../../hooks/useHealth', () => ({
  useHealth: () => ({ health: mockHealth })
}));

describe('AiInsightsPage AI service indicator', () => {
  it('shows offline when the process is alive but the provider is not configured', async () => {
    mockHealth = { aiService: true, aiMode: 'unconfigured', aiProviderReachable: true };
    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Service online - provider not configured')).toBeInTheDocument();
    });
    const dot = document.querySelector('.ai-insights-provider .status-dot');
    expect(dot.className).toContain('offline');
    expect(dot.className).not.toContain('online');
  });

  it('shows offline when the process is alive but the configured provider is unreachable', async () => {
    mockHealth = { aiService: true, aiMode: 'groq', aiProviderReachable: false };
    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/provider unavailable/i)).toBeInTheDocument();
    });
    const dot = document.querySelector('.ai-insights-provider .status-dot');
    expect(dot.className).toContain('offline');
  });

  it('shows online only when the provider is genuinely configured and reachable', async () => {
    mockHealth = { aiService: true, aiMode: 'groq', aiProviderReachable: true };
    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/provider configured/i)).toBeInTheDocument();
    });
    const dot = document.querySelector('.ai-insights-provider .status-dot');
    expect(dot.className).toContain('online');
  });
});
