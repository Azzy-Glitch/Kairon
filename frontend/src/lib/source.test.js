import { describe, expect, it } from 'vitest';
import { resolveSource, buildInstallationLookup, countBySource } from './source';

describe('resolveSource', () => {
  it('resolves .NET from the application name heuristic when no installation link exists', () => {
    expect(resolveSource({ application: 'DotnetSdkDemoApp' })).toBe('dotnet');
  });

  it('resolves Python from the application name heuristic', () => {
    expect(resolveSource({ application: 'PythonSdkDemoApp' })).toBe('python');
  });

  it('falls back to unknown when neither name nor installation link identifies a source', () => {
    expect(resolveSource({ application: 'OrderProcessingService' })).toBe('unknown');
  });

  it('respects an explicit __source set by the caller (e.g. the Machines page) over any heuristic', () => {
    expect(resolveSource({ __source: 'agent', application: 'PythonSdkDemoApp' })).toBe('agent');
  });

  it('prefers a real installation-link match over the naming heuristic', () => {
    const lookup = buildInstallationLookup(
      [{ id: 'app-1', name: 'OrderProcessingService' }],
      [{ applicationId: 'app-1', sdkType: 'python' }]
    );
    // Name alone gives no heuristic signal ("OrderProcessingService" mentions neither dotnet nor
    // python), but the installation link resolves it correctly.
    expect(resolveSource({ application: 'OrderProcessingService' }, lookup)).toBe('python');
  });

  it('returns unknown for a null/undefined record rather than throwing', () => {
    expect(resolveSource(null)).toBe('unknown');
  });
});

describe('countBySource', () => {
  it('counts every record exactly once, "all" equal to the total', () => {
    const records = [{ application: 'DotnetSdkDemoApp' }, { application: 'PythonSdkDemoApp' }, { application: 'DotnetSdkDemoApp' }];
    const counts = countBySource(records);
    expect(counts).toEqual({ all: 3, dotnet: 2, python: 1, agent: 0, unknown: 0 });
  });
});
