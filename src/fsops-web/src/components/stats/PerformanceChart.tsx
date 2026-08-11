import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

import type { StatsPerformancePoint } from '@/types/stats'

interface PerformanceChartProps {
  points: StatsPerformancePoint[]
}

function formatAxisDate(dateUtc: string): string {
  const date = new Date(`${dateUtc}T00:00:00Z`)
  if (Number.isNaN(date.getTime())) return dateUtc
  return date.toLocaleDateString(undefined, { day: '2-digit', month: 'short', timeZone: 'UTC' })
}

interface TooltipPayloadEntry {
  color: string
  name: string
  value: number
}

function ChartTooltip({ active, payload, label }: { active?: boolean; payload?: TooltipPayloadEntry[]; label?: string }) {
  if (!active || !payload || payload.length === 0) return null
  return (
    <div className="rounded-md border border-border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-elevation-2">
      <p className="mb-1 font-medium">{label ? formatAxisDate(label) : ''}</p>
      {payload.map((entry) => (
        <p key={entry.name} style={{ color: entry.color }}>
          {entry.name}: {Math.round(entry.value)}%
        </p>
      ))}
    </div>
  )
}

/**
 * On-time performance and load factor over time - docs/PLAN.md dashboards brief. Lazy-loaded (see
 * Stats.tsx) so recharts is only ever fetched by a player who opens this page, exactly like
 * maplibre-gl only loads for Dashboard/Routes/Fly. Only days with at least one measurable sector
 * are plotted - a quiet stretch is simply absent from the line rather than rendered as a
 * fabricated 0%, matching the "null means not measured" rule the reputation card already follows.
 */
export function PerformanceChart({ points }: PerformanceChartProps) {
  return (
    <ResponsiveContainer width="100%" height={280}>
      <LineChart data={points} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
        <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
        <XAxis
          dataKey="dateUtc"
          tickFormatter={formatAxisDate}
          stroke="hsl(var(--muted-foreground))"
          fontSize={12}
          tickLine={false}
          axisLine={{ stroke: 'hsl(var(--border))' }}
        />
        <YAxis
          domain={[0, 100]}
          tickFormatter={(v: number) => `${v}%`}
          stroke="hsl(var(--muted-foreground))"
          fontSize={12}
          tickLine={false}
          axisLine={{ stroke: 'hsl(var(--border))' }}
          width={44}
        />
        <Tooltip content={<ChartTooltip />} />
        <Legend wrapperStyle={{ fontSize: 12, color: 'hsl(var(--muted-foreground))' }} />
        <Line
          type="monotone"
          dataKey="onTimePercent"
          name="On time"
          stroke="hsl(var(--chart-1))"
          strokeWidth={2}
          dot={false}
          connectNulls
        />
        <Line
          type="monotone"
          dataKey="loadFactorPercent"
          name="Load factor"
          stroke="hsl(var(--chart-4))"
          strokeWidth={2}
          dot={false}
          connectNulls
        />
      </LineChart>
    </ResponsiveContainer>
  )
}

export default PerformanceChart
