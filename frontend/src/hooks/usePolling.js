import { useEffect, useRef } from 'react';

/**
 * Repeats a callback on an interval.
 *
 * Polling rather than WebSockets or SSE is a deliberate choice the frontend PRD (section 5)
 * endorses: the backend has no push channel today, and adding one would be new infrastructure for
 * a demo-scale refresh rate.
 *
 * Two things make it efficient enough to be honest about:
 *  - it pauses while the browser tab is hidden, so a backgrounded dashboard costs nothing;
 *  - it never overlaps runs, so a slow backend cannot accumulate a queue of in-flight requests.
 */
export function usePolling(callback, intervalMs, { enabled = true, pauseWhenHidden = true } = {}) {
  const savedCallback = useRef(callback);
  const running = useRef(false);

  useEffect(() => {
    savedCallback.current = callback;
  }, [callback]);

  useEffect(() => {
    if (!enabled || !intervalMs) return undefined;

    let cancelled = false;

    const tick = async () => {
      if (cancelled || running.current) return;
      if (pauseWhenHidden && typeof document !== 'undefined' && document.hidden) return;

      running.current = true;
      try {
        await savedCallback.current();
      } catch {
        // The caller owns error state; a failed poll must not tear down the interval, or a single
        // backend blip would silently stop all refreshing.
      } finally {
        running.current = false;
      }
    };

    const id = setInterval(tick, intervalMs);

    // Refresh immediately when the operator comes back to the tab, rather than making them wait
    // out the remainder of an interval.
    const onVisible = () => {
      if (typeof document !== 'undefined' && !document.hidden) tick();
    };

    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', onVisible);
    }

    return () => {
      cancelled = true;
      clearInterval(id);
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', onVisible);
      }
    };
  }, [enabled, intervalMs, pauseWhenHidden]);
}

export default usePolling;
