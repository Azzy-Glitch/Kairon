import { useCallback, useEffect, useRef, useState } from 'react';
import { AsyncState } from '../types/incident';

/**
 * Runs an async loader and reports every state the frontend PRD (section 19) asks for:
 * loading, success, empty, error, and a retry.
 *
 * The `isEmpty` predicate matters: "loaded successfully and there is nothing" is a different
 * operator experience from "still loading", and conflating them is how dashboards end up showing
 * a permanent spinner on a healthy system.
 */
export function useAsync(loader, { immediate = true, isEmpty, deps = [] } = {}) {
  const [state, setState] = useState(immediate ? AsyncState.Loading : AsyncState.Idle);
  const [data, setData] = useState(null);
  const [error, setError] = useState(null);
  const [lastUpdated, setLastUpdated] = useState(null);

  // Guards against setting state after unmount, and against a slow earlier request landing on top
  // of a newer one.
  const mounted = useRef(true);
  const requestId = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const run = useCallback(
    async ({ silent = false } = {}) => {
      const id = ++requestId.current;

      if (!silent) setState(AsyncState.Loading);

      try {
        const result = await loader();

        if (!mounted.current || id !== requestId.current) return result;

        setData(result);
        setError(null);
        setLastUpdated(new Date());
        setState(isEmpty?.(result) ? AsyncState.Empty : AsyncState.Success);
        return result;
      } catch (err) {
        if (!mounted.current || id !== requestId.current) throw err;

        setError(err);
        setState(AsyncState.Error);
        throw err;
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    deps
  );

  useEffect(() => {
    if (!immediate) return;

    // A failed background refresh is already reflected in `error`; rethrowing here would surface
    // as an unhandled rejection for no benefit.
    run().catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return {
    state,
    data,
    error,
    lastUpdated,
    isLoading: state === AsyncState.Loading,
    isError: state === AsyncState.Error,
    isEmpty: state === AsyncState.Empty,
    isSuccess: state === AsyncState.Success,
    reload: run,
    setData
  };
}

export default useAsync;
