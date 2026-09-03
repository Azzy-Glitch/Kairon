import { describe, expect, it } from 'vitest';
import {
  getActionLabel,
  getRuleLabel,
  getSignalLabel,
  getPhaseLabel,
  sortBySeverity,
  compareSeverity,
  formatRelativeTime
} from './labels';

describe('labels', () => {
  it('maps every real backend action type to a human, non-identifier string', () => {
    expect(getActionLabel('DisableDemoRetryLoop')).toBe('Turn off the retry loop');
    expect(getActionLabel('ReduceDemoWorkerConcurrency')).toBe('Reduce worker concurrency');
  });

  it('maps every real detection RuleId to a human string', () => {
    expect(getRuleLabel('latency-threshold')).toBe('Response time over limit');
    expect(getRuleLabel('repeated-errors')).toBe('Repeated errors on one endpoint');
  });

  it('maps every real signal metric name to a human string', () => {
    expect(getSignalLabel('errorRate')).toBe('Error rate');
    expect(getSignalLabel('cpu')).toBe('CPU usage');
  });

  it('maps every real IncidentStatus/IncidentEventTypes value to a human string', () => {
    expect(getPhaseLabel('ai-orchestrator')).toBe('ai-orchestrator'); // not a phase value, falls through
    expect(getPhaseLabel('AiRequestStarted')).toBe('Investigation started');
    expect(getPhaseLabel('AwaitingApproval')).toBe('Awaiting approval');
  });

  it('falls back to the raw value rather than throwing on an unknown identifier', () => {
    expect(getActionLabel('SomeFutureToolNobodyMappedYet')).toBe('SomeFutureToolNobodyMappedYet');
  });

  it('sorts severity Critical -> High -> Medium -> Low, everywhere', () => {
    const items = [{ severity: 'Medium' }, { severity: 'Critical' }, { severity: 'Low' }, { severity: 'High' }];
    expect(sortBySeverity(items).map((i) => i.severity)).toEqual(['Critical', 'High', 'Medium', 'Low']);
  });

  it('places an unknown severity value after every known one', () => {
    expect(compareSeverity('Low', 'SomethingUnknown')).toBeLessThan(0);
  });

  it('formats a very recent timestamp as relative time', () => {
    expect(formatRelativeTime(new Date().toISOString())).toBe('Just now');
  });
});
