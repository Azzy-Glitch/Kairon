import React from 'react';

/**
 * A minimal trend line for a series of real samples (frontend PRD section 4: Telemetry Snapshot).
 *
 * Deliberately hand-rolled inline SVG rather than a charting dependency - these are single-series
 * trend lines over at most a few dozen points, which does not justify pulling in a charting library
 * (frontend PRD section 21: no new infrastructure without demonstrated need).
 *
 * Renders nothing (not a flat placeholder line) when there are fewer than two real values, so an
 * empty series reads as "no data yet" rather than a fabricated flat trend.
 */
export default function Sparkline({ values = [], width = 100, height = 28, color = 'currentColor', strokeWidth = 1.75 }) {
  const points = (values || []).filter((v) => typeof v === 'number' && Number.isFinite(v));
  if (points.length < 2) return null;

  const min = Math.min(...points);
  const max = Math.max(...points);
  const span = max - min || 1;

  const coords = points.map((v, i) => {
    const x = (i / (points.length - 1)) * (width - strokeWidth) + strokeWidth / 2;
    const y = height - strokeWidth / 2 - ((v - min) / span) * (height - strokeWidth);
    return [x, y];
  });

  const path = coords.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
  const [lastX, lastY] = coords[coords.length - 1];
  const areaPath = `${path} L${lastX.toFixed(1)},${height} L${coords[0][0].toFixed(1)},${height} Z`;
  const gradientId = `spark-fill-${Math.round(min * 1000) % 99991}-${Math.round(max * 1000) % 99991}`;

  return (
    <svg
      className="sparkline"
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      aria-hidden="true"
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.28" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={areaPath} fill={`url(#${gradientId})`} stroke="none" />
      <path d={path} fill="none" stroke={color} strokeWidth={strokeWidth} strokeLinejoin="round" strokeLinecap="round" />
      <circle cx={lastX} cy={lastY} r={strokeWidth + 0.75} fill={color} />
    </svg>
  );
}
