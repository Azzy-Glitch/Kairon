import { useCallback } from 'react';
import { telemetryApi } from '../api/index';
import useAsync from './useAsync';
import usePolling from './usePolling';

/** Recent raw metric samples for one service - the source for a per-service sparkline
 * (e.g. the Services page), scoped server-side so it never mixes another service's readings in. */
export function useServiceMetrics(service, { pollMs = 8000 } = {}) {
  const loader = useCallback(() => {
    if (!service) return Promise.resolve([]);
    return telemetryApi.getMetrics(undefined, service);
  }, [service]);

  const async = useAsync(loader, { isEmpty: (data) => !data || data.length === 0, deps: [service] });

  usePolling(() => async.reload({ silent: true }), pollMs, { enabled: Boolean(service) });

  return async;
}
