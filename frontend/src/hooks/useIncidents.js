import { useCallback, useMemo, useState } from 'react';
import { incidentsApi } from '../api/index';
import { sortIncidents } from '../services/incidentService';
import useAsync from './useAsync';
import usePolling from './usePolling';

const DEFAULT_INTERVAL = 4000;

/** Live incident feed. */
export function useIncidents({ status = 'active', severity, service, pollMs = DEFAULT_INTERVAL } = {}) {
  const loader = useCallback(
    () => incidentsApi.listIncidents({ status, severity, service }),
    [status, severity, service]
  );

  const async = useAsync(loader, {
    isEmpty: (data) => !data || data.length === 0,
    deps: [status, severity, service]
  });

  // Silent refreshes: a live feed that flashes a spinner every four seconds is unreadable.
  usePolling(() => async.reload({ silent: true }), pollMs);

  const incidents = useMemo(() => sortIncidents(async.data || []), [async.data]);

  return { ...async, incidents };
}

/** One incident, polled while it is still moving through the lifecycle. */
export function useIncident(incidentId, { pollMs = 3000 } = {}) {
  const loader = useCallback(() => {
    if (!incidentId) return Promise.resolve(null);
    return incidentsApi.getIncident(incidentId);
  }, [incidentId]);

  const async = useAsync(loader, { isEmpty: (data) => !data, deps: [incidentId] });

  usePolling(() => async.reload({ silent: true }), pollMs, { enabled: Boolean(incidentId) });

  return async;
}

/** Dashboard aggregate: counts, severity mix, live metrics and component health. */
export function useDashboard({ pollMs = DEFAULT_INTERVAL } = {}) {
  const loader = useCallback(() => incidentsApi.getDashboard(), []);
  const async = useAsync(loader, { deps: [] });

  usePolling(() => async.reload({ silent: true }), pollMs);

  return async;
}

export function useRemediationTools() {
  const loader = useCallback(() => incidentsApi.getTools(), []);
  return useAsync(loader, { isEmpty: (data) => !data || data.length === 0, deps: [] });
}

/**
 * Approve / reject / cancel, with the in-flight and error state the operator UI needs.
 *
 * These are the only calls in the frontend that cause anything to execute, so they are kept
 * separate from the read hooks and always require an explicit operator identity.
 */
export function useIncidentActions(onChanged) {
  const [busyActionId, setBusyActionId] = useState(null);
  const [actionError, setActionError] = useState(null);

  const run = useCallback(
    async (actionId, work) => {
      setBusyActionId(actionId);
      setActionError(null);

      try {
        const result = await work();
        await onChanged?.(result);
        return result;
      } catch (error) {
        setActionError(error);
        throw error;
      } finally {
        setBusyActionId(null);
      }
    },
    [onChanged]
  );

  const approve = useCallback(
    (incidentId, actionId, approvedBy, note) =>
      run(actionId, () => incidentsApi.approveAction(incidentId, actionId, approvedBy, note)),
    [run]
  );

  const reject = useCallback(
    (incidentId, actionId, rejectedBy, reason) =>
      run(actionId, () => incidentsApi.rejectAction(incidentId, actionId, rejectedBy, reason)),
    [run]
  );

  const cancel = useCallback(
    (incidentId, cancelledBy, reason) =>
      run('cancel', () => incidentsApi.cancelIncident(incidentId, cancelledBy, reason)),
    [run]
  );

  const investigate = useCallback(
    (incidentId) => run('investigate', () => incidentsApi.investigate(incidentId)),
    [run]
  );

  return {
    approve,
    reject,
    cancel,
    investigate,
    busyActionId,
    actionError,
    clearActionError: () => setActionError(null)
  };
}
