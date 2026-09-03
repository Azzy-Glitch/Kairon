import React from 'react';
import { AreaChart, Area, ReferenceLine, ResponsiveContainer, Tooltip, YAxis } from 'recharts';

function formatDelta(deltaPercent) {
  if (deltaPercent === null || deltaPercent === undefined || Number.isNaN(deltaPercent)) return null;
  const rounded = Math.round(deltaPercent);
  if (rounded === 0) return { text: '0%', dir: 'flat' };
  return { text: `${Math.abs(rounded)}%`, dir: rounded > 0 ? 'up' : 'down' };
}

function TileTooltip({ active, payload, label, unit }) {
  if (!active || !payload?.length) return null;
  return (
    <div className="ui-metric-tooltip">
      <div className="ui-metric-tooltip-value">
        {payload[0].value}
        {unit}
      </div>
      <div className="ui-metric-tooltip-time">{label}</div>
    </div>
  );
}

/**
 * MetricTile (redesign brief section 7). Replaces a static number-with-decorative-bar with a
 * live area chart, a dashed threshold reference line, and a headline number that turns
 * --critical only while the current value is over threshold - the chart line itself always stays
 * its series color, per the brief's explicit "threshold as a line, not a colour" rule.
 *
 * This is the presentational shell: it renders whatever `data` it is given. The rolling-buffer /
 * adaptive-polling logic that produces that data belongs to whichever page uses this tile (brief
 * section 7's live-charts phase), not to the tile itself - keeps this component reusable for
 * both genuinely live metrics and a fixed demo/preview dataset.
 */
export default function MetricTile({
  label,
  value,
  unit = '',
  data = [],
  dataKey = 'v',
  timeKey = 't',
  seriesColor = 'var(--series-1)',
  threshold,
  thresholdLabel,
  domain,
  deltaPercent,
  breachedForLabel,
  className = ''
}) {
  const isBreached = typeof threshold === 'number' && typeof value === 'number' && value > threshold;
  const delta = formatDelta(deltaPercent);
  const hasSamples = data.length > 0;

  return (
    <div className={`ui-metric-tile ${isBreached ? 'ui-metric-tile-breached' : ''} ${className}`}>
      <div className="ui-metric-tile-head">
        <span className="ui-metric-tile-label">{label}</span>
        {delta && (
          <span className={`ui-metric-tile-delta ui-metric-tile-delta-${delta.dir}`}>
            {delta.dir === 'up' ? '▲' : delta.dir === 'down' ? '▼' : '—'} {delta.text}
          </span>
        )}
        {isBreached && <span className="ui-metric-tile-warn" aria-label="Over threshold">{'⚠️'}</span>}
      </div>

      <div
        className={`ui-metric-tile-value ${isBreached ? 'ui-metric-tile-value-breached' : ''}`}
        aria-live="polite"
      >
        {value === null || value === undefined ? '——' : value}
        {value !== null && value !== undefined ? unit : ''}
      </div>

      <div className="ui-metric-tile-chart">
        {hasSamples ? (
          <ResponsiveContainer width="100%" height={64}>
            <AreaChart data={data} margin={{ top: 4, right: 4, bottom: 0, left: 0 }}>
              <YAxis hide domain={domain || ['auto', 'auto']} />
              <defs>
                <linearGradient id={`metric-fill-${label}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor={seriesColor} stopOpacity={0.1} />
                  <stop offset="100%" stopColor={seriesColor} stopOpacity={0} />
                </linearGradient>
              </defs>
              {typeof threshold === 'number' && (
                <ReferenceLine
                  y={threshold}
                  stroke="var(--threshold)"
                  strokeDasharray="4 3"
                  label={{ value: thresholdLabel ?? `${threshold}${unit}`, position: 'right', fill: 'var(--threshold)', fontSize: 10 }}
                />
              )}
              <Area
                type="monotone"
                dataKey={dataKey}
                stroke={seriesColor}
                strokeWidth={1.5}
                fill={`url(#metric-fill-${label})`}
                isAnimationActive={false}
                dot={false}
              />
              <Tooltip content={<TileTooltip unit={unit} />} labelKey={timeKey} />
            </AreaChart>
          </ResponsiveContainer>
        ) : (
          <div className="ui-metric-tile-empty">No data yet</div>
        )}
      </div>

      <div className="ui-metric-tile-foot">
        <span>{typeof threshold === 'number' ? `Threshold ${threshold}${unit}` : ''}</span>
        {breachedForLabel && <span className="ui-metric-tile-breach-label">{breachedForLabel}</span>}
      </div>
    </div>
  );
}
