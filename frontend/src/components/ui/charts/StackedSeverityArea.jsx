import React from 'react';
import { AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';

const SEVERITY_COLOR = { Critical: 'var(--critical)', High: 'var(--high)', Medium: 'var(--medium)' };

/**
 * "Incidents over time, stacked by severity" (Overview - brief section 7 chart table). data rows
 * look like { t: '00:00', Critical: 2, High: 1, Medium: 0 }.
 */
export default function StackedSeverityArea({ data, height = 220 }) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <AreaChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid stroke="var(--grid)" strokeOpacity={0.2} vertical={false} />
        <XAxis dataKey="t" tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} />
        <YAxis tick={{ fill: 'var(--text-muted)', fontSize: 11 }} axisLine={false} tickLine={false} allowDecimals={false} />
        <Tooltip
          contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 'var(--radius-input)', fontSize: 12 }}
        />
        {Object.keys(SEVERITY_COLOR).map((sev) => (
          <Area
            key={sev}
            type="monotone"
            dataKey={sev}
            stackId="severity"
            stroke={SEVERITY_COLOR[sev]}
            fill={SEVERITY_COLOR[sev]}
            fillOpacity={0.15}
            strokeWidth={1.5}
            isAnimationActive={false}
          />
        ))}
      </AreaChart>
    </ResponsiveContainer>
  );
}
