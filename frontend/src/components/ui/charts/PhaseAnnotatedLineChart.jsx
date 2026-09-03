import React from 'react';
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ReferenceLine, ResponsiveContainer } from 'recharts';

const SERIES_COLORS = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-5)'];

/**
 * The Demo Center chart (brief section 7: "the demo's money shot") - all four simulation metrics
 * on one shared time axis, with phase transitions (Normal -> Failure -> Detected -> ...) as
 * vertical annotation lines. The one deliberate motion moment in the whole app (brief section 9)
 * lives here, via the ReferenceLine's own render - everything else in this chart is quiet.
 *
 * data: [{ t, cpu, latency, errorRate, retries }]
 * phaseMarkers: [{ t, label }] - a vertical line + label at the moment each phase started.
 */
export default function PhaseAnnotatedLineChart({ data, seriesKeys, phaseMarkers = [], height = 280 }) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={data} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
        <CartesianGrid stroke="var(--grid)" strokeOpacity={0.2} vertical={false} />
        <XAxis dataKey="t" tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} />
        <YAxis tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} />
        <Tooltip
          contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius-input)', fontSize: 12 }}
        />
        <Legend wrapperStyle={{ fontSize: 12, color: 'var(--text-muted)' }} />
        {seriesKeys.map((key, i) => (
          <Line
            key={key.dataKey}
            type="monotone"
            dataKey={key.dataKey}
            name={key.name}
            stroke={SERIES_COLORS[i % SERIES_COLORS.length]}
            strokeWidth={1.5}
            dot={false}
            isAnimationActive={false}
          />
        ))}
        {phaseMarkers.map((marker) => (
          <ReferenceLine
            key={marker.t}
            x={marker.t}
            stroke="var(--text-muted)"
            strokeDasharray="3 3"
            label={{ value: marker.label, position: 'top', fill: 'var(--text-secondary)', fontSize: 11 }}
          />
        ))}
      </LineChart>
    </ResponsiveContainer>
  );
}
