/**
 * Fixed Y-domain per metric type (redesign brief section 7) - so a chart's line doesn't rescale
 * every tick. Percent metrics are always [0, 100]; anything unbounded (latency, queue depth, rate
 * metrics) is [0, max(threshold * 1.5, peak observed so far)].
 */
export function domainForMetric(kind, { threshold, peak = 0 } = {}) {
  if (kind === 'percent') return [0, 100];
  const upper = Math.max((threshold ?? 0) * 1.5, peak, 1);
  return [0, upper];
}
