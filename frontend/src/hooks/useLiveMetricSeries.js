import { useCallback, useRef } from 'react';
import { usePolling } from './usePolling';
import { useRollingBuffer } from './useRollingBuffer';

/**
 * Ties together the rolling-window buffer and adaptive polling every live metric tile needs
 * (redesign brief section 7), so pages don't each reimplement the same fetch/append/backoff
 * dance. Built on the existing usePolling hook (already handles document.hidden pausing) rather
 * than a new polling implementation.
 *
 * fetchSample: async () => { t: string, v: number } | null - null means "skip this tick" (e.g. the
 * source went quiet) rather than pushing a bad sample.
 * isLive: whether a simulation is running / an incident is open right now - drives the interval
 * choice; the hook re-subscribes automatically when this flips (usePolling re-runs its effect
 * whenever intervalMs changes).
 */
export function useLiveMetricSeries(fetchSample, { isLive = false, liveIntervalMs = 1500, idleIntervalMs = 10000, capacity = 60 } = {}) {
  const [samples, push, reset] = useRollingBuffer(capacity);
  const fetchRef = useRef(fetchSample);
  fetchRef.current = fetchSample;

  const tick = useCallback(async () => {
    const sample = await fetchRef.current();
    if (sample) push(sample);
  }, [push]);

  usePolling(tick, isLive ? liveIntervalMs : idleIntervalMs);

  return { samples, reset };
}
