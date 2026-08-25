import { describe, expect, it } from 'vitest';
import {
  buildLifecycle,
  confidenceBand,
  executedActions,
  formatConfidence,
  formatMetricValue,
  isResolvedByBackend,
  latestVerification,
  pendingAction,
  percentChange,
  relativeTime,
  sortIncidents,
  verificationFailed
} from './incidentService';
import { IncidentStatus, RemediationStatus, VerificationStatus } from '../types/incident';

const incident = (overrides = {}) => ({
  id: 'i1',
  status: IncidentStatus.Detected,
  severity: 'Medium',
  updatedAt: '2026-08-25T12:00:00Z',
  actions: [],
  verifications: [],
  timeline: [],
  ...overrides
});

describe('sortIncidents', () => {
  it('puts incidents awaiting approval first', () => {
    const sorted = sortIncidents([
      incident({ id: 'a', severity: 'Critical' }),
      incident({ id: 'b', severity: 'Low', status: IncidentStatus.AwaitingApproval })
    ]);

    expect(sorted[0].id).toBe('b');
  });

  it('sorts by severity after that', () => {
    const sorted = sortIncidents([
      incident({ id: 'low', severity: 'Low' }),
      incident({ id: 'critical', severity: 'Critical' }),
      incident({ id: 'medium', severity: 'Medium' })
    ]);

    expect(sorted.map((i) => i.id)).toEqual(['critical', 'medium', 'low']);
  });

  it('pushes terminal incidents to the bottom regardless of severity', () => {
    const sorted = sortIncidents([
      incident({ id: 'resolved', severity: 'Critical', status: IncidentStatus.Resolved }),
      incident({ id: 'open', severity: 'Low' })
    ]);

    expect(sorted[0].id).toBe('open');
  });

  it('does not mutate the input', () => {
    const input = [incident({ id: 'a', severity: 'Low' }), incident({ id: 'b', severity: 'Critical' })];
    sortIncidents(input);

    expect(input[0].id).toBe('a');
  });

  it('handles an empty list', () => {
    expect(sortIncidents([])).toEqual([]);
    expect(sortIncidents()).toEqual([]);
  });
});

describe('buildLifecycle', () => {
  it('marks stages before the current one as done', () => {
    const stages = buildLifecycle(incident({ status: IncidentStatus.Predicted }));

    expect(stages.find((s) => s.key === IncidentStatus.Detected).state).toBe('done');
    expect(stages.find((s) => s.key === IncidentStatus.Predicted).state).toBe('active');
    expect(stages.find((s) => s.key === IncidentStatus.Resolved).state).toBe('pending');
  });

  it('marks the whole rail done for a resolved incident', () => {
    const stages = buildLifecycle(incident({ status: IncidentStatus.Resolved }));

    expect(stages.at(-1).state).toBe('active');
    expect(stages[0].state).toBe('done');
  });

  it('shows where a failed incident stopped', () => {
    const stages = buildLifecycle(
      incident({
        status: IncidentStatus.Failed,
        timeline: [
          { newState: IncidentStatus.Detected },
          { newState: IncidentStatus.Investigating },
          { newState: IncidentStatus.Diagnosed }
        ]
      })
    );

    expect(stages.find((s) => s.state === 'failed')).toBeTruthy();
  });

  it('returns nothing without an incident', () => {
    expect(buildLifecycle(null)).toEqual([]);
  });
});

describe('action helpers', () => {
  it('finds the action awaiting approval', () => {
    const found = pendingAction(
      incident({
        actions: [
          { id: 'a', status: RemediationStatus.Rejected },
          { id: 'b', status: RemediationStatus.AwaitingApproval }
        ]
      })
    );

    expect(found.id).toBe('b');
  });

  it('returns null when nothing is pending', () => {
    expect(pendingAction(incident())).toBeNull();
    expect(pendingAction(null)).toBeNull();
  });

  it('lists only actions that actually ran or tried to', () => {
    const executed = executedActions(
      incident({
        actions: [
          { id: 'a', status: RemediationStatus.AwaitingApproval },
          { id: 'b', status: RemediationStatus.Executed },
          { id: 'c', status: RemediationStatus.Failed }
        ]
      })
    );

    expect(executed.map((a) => a.id)).toEqual(['b', 'c']);
  });

  it('picks the newest verification', () => {
    const latest = latestVerification(
      incident({
        verifications: [
          { id: 'old', startedAt: '2026-08-25T10:00:00Z' },
          { id: 'new', startedAt: '2026-08-25T12:00:00Z' }
        ]
      })
    );

    expect(latest.id).toBe('new');
  });
});

describe('resolution is backend-owned', () => {
  it('reports resolved only when the backend says so', () => {
    expect(isResolvedByBackend(incident({ status: IncidentStatus.Resolved }))).toBe(true);
    expect(isResolvedByBackend(incident({ status: IncidentStatus.Verifying }))).toBe(false);
  });

  it('does not infer resolution from a passed verification', () => {
    // The frontend must never decide an incident is over (frontend PRD section 13).
    const stillVerifying = incident({
      status: IncidentStatus.Verifying,
      verifications: [{ id: 'v', status: VerificationStatus.Passed, startedAt: '2026-08-25T12:00:00Z' }]
    });

    expect(isResolvedByBackend(stillVerifying)).toBe(false);
  });

  it('flags a failed verification', () => {
    expect(
      verificationFailed(
        incident({ verifications: [{ status: VerificationStatus.Failed, startedAt: '2026-08-25T12:00:00Z' }] })
      )
    ).toBe(true);
  });

  it('treats an inconclusive verification as not confirmed', () => {
    expect(
      verificationFailed(
        incident({ verifications: [{ status: VerificationStatus.Inconclusive, startedAt: '2026-08-25T12:00:00Z' }] })
      )
    ).toBe(true);
  });
});

describe('formatting', () => {
  it('always labels confidence as an estimate', () => {
    expect(formatConfidence(0.92)).toBe('92% (model estimate)');
  });

  it('says so when confidence is absent rather than showing zero', () => {
    expect(formatConfidence(null)).toBe('not reported');
    expect(formatConfidence(undefined)).toBe('not reported');
  });

  it('bands confidence', () => {
    expect(confidenceBand(0.9)).toBe('high');
    expect(confidenceBand(0.7)).toBe('moderate');
    expect(confidenceBand(0.3)).toBe('low');
    expect(confidenceBand(null)).toBe('unknown');
  });

  it('formats metric values with units', () => {
    expect(formatMetricValue(94.23, '%')).toBe('94.2%');
    expect(formatMetricValue(2400.6, 'ms')).toBe('2401ms');
    expect(formatMetricValue(null, '%')).toBe('--');
  });

  it('computes percentage change', () => {
    expect(percentChange(100, 25)).toBe(-75);
    expect(percentChange(100, 150)).toBe(50);
    expect(percentChange(0, 0)).toBe(0);
    expect(percentChange(null, 10)).toBeNull();
  });

  it('renders relative times', () => {
    expect(relativeTime(new Date().toISOString())).toBe('just now');
    expect(relativeTime(null)).toBe('--');
    expect(relativeTime('not a date')).toBe('--');
  });
});
