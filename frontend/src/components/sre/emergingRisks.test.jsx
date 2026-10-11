import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';

const state = vi.hoisted(() => ({ data: null }));
vi.mock('../../hooks/useIncidents', () => ({
  useIncidents: () => ({ incidents: [] }),
  useIncidentDetails: () => ({ data: [] }),
  useEmergingRisks: () => ({ data: state.data })
}));

import { EmergingRisks } from './AiInsightsPage';

describe('Emerging risks', () => {
  it('shows each risk with its observed trend, consequence, preventive action and a horizon only when given', () => {
    state.data = {
      windowMinutes: 60,
      risks: [
        { kind: 'capacity', service: 'Checkout', environment: 'Development', component: 'memory', title: 'Memory rising',
          observation: 'Memory rising: from about 50% to 79.5% over 29 minutes (+60%/hour, trend fit R² 0.99).',
          consequence: 'The process may be killed or crash when memory runs out.', preventiveAction: 'Check for a leak.',
          confidence: 0.86, samples: 30, horizonMinutes: 6 },
        { kind: 'dependency', service: 'Checkout', environment: 'Development', component: 'payments.example:443/charge',
          title: 'Repeated dependency failures', observation: '4 of 6 calls to payments.example:443/charge failed in the last hour (avg 120 ms).',
          consequence: 'Requests that depend on it will keep failing.', preventiveAction: 'Check its availability.', confidence: 0.6, samples: 6 }
      ],
      insufficientData: [{ service: 'Orders', environment: 'Production', reason: 'too few samples' }]
    };

    render(<EmergingRisks />);

    expect(screen.getByText('Memory rising')).toBeInTheDocument();
    expect(screen.getByText(/threshold in ~6 min if the trend continues/)).toBeInTheDocument();
    expect(screen.getByText(/4 of 6 calls/)).toBeInTheDocument();
    expect(screen.getAllByText(/if the trend continues/)).toHaveLength(1); // no horizon for the dependency risk
    expect(screen.getByText(/Not enough data yet for: Orders \(Production\)/)).toBeInTheDocument();
    expect(screen.getByText(/not certainties/)).toBeInTheDocument();
  });

  it('says plainly when nothing is trending toward trouble', () => {
    state.data = { windowMinutes: 60, risks: [], insufficientData: [] };
    render(<EmergingRisks />);
    expect(screen.getByText(/No emerging risks in the last 60 minutes/)).toBeInTheDocument();
  });
});
