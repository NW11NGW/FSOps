import { CartesianGrid, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

import type { FareCurvePoint } from '@/types/planning'

interface FareCurveChartProps {
  points: FareCurvePoint[]
  /** The fare the player is currently considering, drawn as a vertical marker so they can see
   *  where they sit on the curve rather than having to find it. */
  currentFare: number
  referenceFare: number
  bestProfitFare: number
  /** Converts a base-unit money figure to the user's currency for the axes and tooltip. The chart
   *  never formats money itself - see lib/format.ts, money is stored in one base unit and
   *  converted only at the point of display. */
  formatMoney: (baseAmount: number) => string
}

interface TooltipPayloadEntry {
  color: string
  name: string
  value: number
  payload: FareCurvePoint
}

function CurveTooltip({
  active,
  payload,
  formatMoney,
}: {
  active?: boolean
  payload?: TooltipPayloadEntry[]
  formatMoney: (baseAmount: number) => string
}) {
  const point = active ? payload?.[0]?.payload : undefined
  if (!point) return null
  return (
    <div className="rounded-md border border-border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-elevation-2">
      <p className="mb-1 font-medium">Fare {formatMoney(point.fare)}</p>
      <p className="text-muted-foreground">
        {point.paxBooked} passengers ({point.loadFactorPercent.toFixed(0)}% full)
      </p>
      <p style={{ color: 'hsl(var(--chart-1))' }}>Revenue {formatMoney(point.revenue)}</p>
      <p style={{ color: 'hsl(var(--chart-4))' }}>Profit {formatMoney(point.profit)}</p>
    </div>
  )
}

/**
 * Revenue and profit against fare, with the player's current fare and the best sampled fare marked.
 * The whole point is that the curve turns over: charging more earns more right up until it doesn't,
 * and this is where a player can see that for themselves instead of being told a single number.
 *
 * Lazy-loaded by FarePricingDialog so recharts is only fetched by a player who actually opens the
 * fare workbench - the same treatment Stats gives its own charts.
 */
export function FareCurveChart({ points, currentFare, referenceFare, bestProfitFare, formatMoney }: FareCurveChartProps) {
  return (
    <ResponsiveContainer width="100%" height={240}>
      <LineChart data={points} margin={{ top: 8, right: 12, left: 0, bottom: 0 }}>
        <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
        <XAxis
          dataKey="fare"
          type="number"
          domain={['dataMin', 'dataMax']}
          tickFormatter={formatMoney}
          stroke="hsl(var(--muted-foreground))"
          fontSize={11}
          tickLine={false}
          axisLine={{ stroke: 'hsl(var(--border))' }}
          minTickGap={28}
        />
        <YAxis
          tickFormatter={formatMoney}
          stroke="hsl(var(--muted-foreground))"
          fontSize={11}
          tickLine={false}
          axisLine={{ stroke: 'hsl(var(--border))' }}
          width={72}
        />
        <Tooltip content={<CurveTooltip formatMoney={formatMoney} />} />
        <ReferenceLine
          x={referenceFare}
          stroke="hsl(var(--muted-foreground))"
          strokeDasharray="4 4"
          label={{ value: 'Suggested', position: 'insideTopLeft', fill: 'hsl(var(--muted-foreground))', fontSize: 10 }}
        />
        <ReferenceLine
          x={bestProfitFare}
          stroke="hsl(var(--success))"
          strokeDasharray="4 4"
          label={{ value: 'Best sampled', position: 'insideTopRight', fill: 'hsl(var(--success))', fontSize: 10 }}
        />
        <ReferenceLine x={currentFare} stroke="hsl(var(--accent))" strokeWidth={2} />
        <Line type="monotone" dataKey="revenue" name="Revenue" stroke="hsl(var(--chart-1))" strokeWidth={2} dot={false} />
        <Line type="monotone" dataKey="profit" name="Profit" stroke="hsl(var(--chart-4))" strokeWidth={2} dot={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}

export default FareCurveChart
