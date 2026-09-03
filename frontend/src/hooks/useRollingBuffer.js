import { useCallback, useRef, useState } from 'react';

/**
 * Rolling-window sample buffer for live metric tiles (redesign brief section 7): keeps the last
 * `capacity` samples in a useRef circular buffer and appends/drops without ever rebuilding the
 * whole history array from scratch. useState mirrors the buffer's *contents* (a new array
 * reference each push, so charts re-render) while the ring itself lives in the ref.
 *
 * Returns [samples, push] - samples is a plain array in chronological order, ready to hand
 * straight to a recharts <AreaChart data={samples}>.
 */
export function useRollingBuffer(capacity = 60) {
  const ringRef = useRef([]);
  const [samples, setSamples] = useState([]);

  const push = useCallback(
    (sample) => {
      const ring = ringRef.current;
      ring.push(sample);
      if (ring.length > capacity) ring.shift();
      // New array reference (not a mutation of the same array) so React actually re-renders -
      // the ring itself is still O(1) amortized push/shift, this slice is just the render payload.
      setSamples(ring.slice());
    },
    [capacity]
  );

  const reset = useCallback(() => {
    ringRef.current = [];
    setSamples([]);
  }, []);

  return [samples, push, reset];
}
