import React from 'react';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';

const MAX_TICK_LABEL_LENGTH = 28;

/** Truncates only what's drawn on the axis - the full label still reaches the tooltip, since
 * recharts computes that from the untouched data point, not from tickFormatter's output. Some
 * callers pass short category names (a service, a title); others (AI-generated root causes) pass
 * full sentences that would otherwise wrap across several lines and blow out the axis column. */
function truncateTick(value) {
  const text = String(value ?? '');
  return text.length > MAX_TICK_LABEL_LENGTH ? `${text.slice(0, MAX_TICK_LABEL_LENGTH - 1)}…` : text;
}

/**
 * Generic horizontal bar chart (brief section 7): used for "Signals correlated per incident"
 * (Overview), "Confidence distribution" and "Recurring root causes" (Insights). No legend - always
 * a single series, per the brief's "no legends when there's one series" rule.
 *
 * data: [{ label, value }], already sorted by the caller (the brief asks for "sorted desc" on
 * recurring causes specifically - sorting is the caller's call, not baked in here).
 */
export default function HorizontalBarChart({ data, color = 'var(--series-2)', height, unit = '' }) {
  const rowHeight = 32;
  const resolvedHeight = height ?? Math.max(120, data.length * rowHeight + 24);

  return (
    <ResponsiveContainer width="100%" height={resolvedHeight}>
      <BarChart data={data} layout="vertical" margin={{ top: 4, right: 24, bottom: 0, left: 8 }}>
        <CartesianGrid stroke="var(--grid)" strokeOpacity={0.2} horizontal={false} />
        <XAxis type="number" tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} allowDecimals={false} />
        <YAxis
          type="category"
          dataKey="label"
          width={160}
          tickFormatter={truncateTick}
          tick={{ fill: 'var(--text-secondary)', fontSize: 12 }}
          axisLine={false}
          tickLine={false}
        />
        <Tooltip
          cursor={{ fill: 'var(--surface-sunken)' }}
          contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius-input)', fontSize: 12, maxWidth: 320 }}
          wrapperStyle={{ zIndex: 10 }}
          formatter={(value) => [`${value}${unit}`, undefined]}
        />
        <Bar dataKey="value" fill={color} radius={[0, 4, 4, 0]} isAnimationActive={false} barSize={16} />
      </BarChart>
    </ResponsiveContainer>
  );
}
