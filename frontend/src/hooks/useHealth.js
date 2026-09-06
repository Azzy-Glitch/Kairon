import { useCallback } from 'react';
import { healthApi } from '../api/index';
import useAsync from './useAsync';
import usePolling from './usePolling';

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
