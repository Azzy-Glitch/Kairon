import React from 'react';
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from 'recharts';

/**
 * "Per-service CPU + latency, dual axis, 5-min window" (Services - brief section 7 chart table).
 * Two series with genuinely different units share one time axis but separate Y scales.
 *
 * data: [{ t, cpu, latency }]
 */
export default function DualAxisLineChart({ data, height = 220 }) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid stroke="var(--grid)" strokeOpacity={0.2} vertical={false} />
        <XAxis dataKey="t" tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} />
        <YAxis
          yAxisId="cpu"
          domain={[0, 100]}
          tick={{ fill: 'var(--series-1)', fontSize: 11 }}
          axisLine={false}
          tickLine={false}
        />
        <YAxis
          yAxisId="latency"
          orientation="right"
          tick={{ fill: 'var(--series-3)', fontSize: 11 }}
          axisLine={false}
          tickLine={false}
        />
        <Tooltip
          contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius-input)', fontSize: 12 }}
        />
        <Legend wrapperStyle={{ fontSize: 12, color: 'var(--text-muted)' }} />
        <Line yAxisId="cpu" type="monotone" dataKey="cpu" name="CPU %" stroke="var(--series-1)" strokeWidth={1.5} dot={false} isAnimationActive={false} />
        <Line yAxisId="latency" type="monotone" dataKey="latency" name="Latency ms" stroke="var(--series-3)" strokeWidth={1.5} dot={false} isAnimationActive={false} />
      </LineChart>
    </ResponsiveContainer>
  );
}
