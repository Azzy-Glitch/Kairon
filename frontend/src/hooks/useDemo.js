import { useCallback, useState } from 'react';
import { demoApi, healthApi } from '../api/index';
import useAsync from './useAsync';
import usePolling from './usePolling';

/** Demo scenario state and controls (frontend PRD section 14). */
export function useDemo({ pollMs = 3000 } = {}) {
  const loader = useCallback(() => demoApi.getDemoState(), []);
  const async = useAsync(loader, { deps: [] });

  const [busy, setBusy] = useState(null);
  const [error, setError] = useState(null);

  usePolling(() => async.reload({ silent: true }), pollMs);

  const run = useCallback(
    async (name, work) => {
      setBusy(name);
      setError(null);

      try {
        const result = await work();
        await async.reload({ silent: true });
        return result;
      } catch (err) {
        setError(err);
        throw err;
      } finally {
        setBusy(null);
      }
    },
    [async]
  );

  return {
    ...async,
    busy,
    error: error || async.error,
    start: () => run('start', demoApi.startSimulation),
    stop: () => run('stop', demoApi.stopSimulation),
    evaluate: () => run('evaluate', demoApi.forceEvaluate)
  };
}

/**
 * Component health for the header. Polled slowly: this answers "is anything down", which does not
 * need a four-second refresh.
 */
export function useHealth({ pollMs = 15000 } = {}) {
  const loader = useCallback(() => healthApi.getHealthStatus(), []);
  const async = useAsync(loader, { deps: [] });

  usePolling(() => async.reload({ silent: true }), pollMs);

  // An unreachable backend is itself the answer, so the fallback reports everything down rather
  // than leaving the header blank.
  const health = async.data || {
    backend: !async.isError,
    database: false,
    aiService: false,
    detectionEnabled: false,
    remediationEnabled: false,
    aiMode: 'unknown'
  };

  return { ...async, health };
}
