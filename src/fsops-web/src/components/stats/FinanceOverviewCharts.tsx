import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'

import type { FinanceCosts, FinanceRoute } from '@/types/finance'

interface FinanceOverviewChartsProps {
  costs: FinanceCosts | null
  routes: FinanceRoute[]
  fmtMoney: (amount: number) => string
}

interface MoneyTooltipPayloadEntry {
  value: number
  payload: { label: string }
}

function makeMoneyTooltip(fmtMoney: (amount: number) => string) {
  return function MoneyTooltip({ active, payload }: { active?: boolean; payload?: MoneyTooltipPayloadEntry[] }) {
    if (!active || !payload || payload.length === 0) return null
    const entry = payload[0]
    if (!entry) return null
    return (
      <div className="rounded-md border border-border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-elevation-2">
        <p className="font-medium">{entry.payload.label}</p>
        <p>{fmtMoney(entry.value)}</p>
      </div>
    )
  }
}

/**
 * Revenue/cost charts for the statistics dashboard. Deliberately renders figures this page does
 * NOT compute: `costs` is FinanceEndpoints' own fixed/variable/revenue split
 * (`GET /finance/costs`) and `routes` is its per-route P&L (`GET /finance/routes`) - see
 * useStatsOverview's own doc for why reusing those, rather than re-deriving the same ledger sums a
 * second time, is the point. Lazy-loaded alongside PerformanceChart so recharts is fetched only
 * once, only on this page.
 */
export function FinanceOverviewCharts({ costs, routes, fmtMoney }: FinanceOverviewChartsProps) {
  const MoneyTooltip = makeMoneyTooltip(fmtMoney)

  const breakdown = costs
    ? [
        { label: 'Revenue', amount: costs.revenue.total, color: 'hsl(var(--chart-4))' },
        { label: 'Fixed costs', amount: Math.abs(costs.fixed.total), color: 'hsl(var(--chart-1))' },
        { label: 'Variable costs', amount: Math.abs(costs.variable.total), color: 'hsl(var(--chart-2))' },
      ]
    : []

  // Top 8 by |profit| so a long route list never crushes the bars unreadably thin - still sorted
  // by profit descending (the order RoutesAsync already returns), so the biggest winner leads.
  const topRoutes = routes.slice(0, 8).map((r) => ({
    label: r.flightNumber ? `${r.departureIcao}-${r.arrivalIcao} (${r.flightNumber})` : `${r.departureIcao}-${r.arrivalIcao}`,
    amount: r.profit,
  }))

  return (
    <div className="grid gap-4 md:grid-cols-2">
      <div>
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Revenue vs cost</p>
        <ResponsiveContainer width="100%" height={240}>
          <BarChart data={breakdown} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" vertical={false} />
            <XAxis dataKey="label" stroke="hsl(var(--muted-foreground))" fontSize={12} tickLine={false} axisLine={{ stroke: 'hsl(var(--border))' }} />
            <YAxis
              tickFormatter={(v: number) => fmtMoney(v)}
              stroke="hsl(var(--muted-foreground))"
              fontSize={11}
              tickLine={false}
              axisLine={{ stroke: 'hsl(var(--border))' }}
              width={72}
            />
            <Tooltip content={<MoneyTooltip />} cursor={{ fill: 'hsl(var(--muted) / 0.4)' }} />
            <Bar dataKey="amount" radius={[4, 4, 0, 0]}>
              {breakdown.map((entry) => (
                <Cell key={entry.label} fill={entry.color} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>

      <div>
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Profit per route</p>
        {topRoutes.length === 0 ? (
          // A chart with no bars and no axis labels reads as broken, not "no data yet" - this
          // section has its own empty condition, independent of the revenue/cost chart alongside
          // it, because a brand-new airline can already have fixed costs (a starter lease) before
          // it has ever completed a flight on a route.
          <div className="flex h-[240px] flex-col items-center justify-center gap-1 rounded-md border border-dashed border-border text-center">
            <p className="text-sm font-medium">No completed flights yet</p>
            <p className="max-w-[220px] text-xs text-muted-foreground">Route profit appears here once a sector on that route has completed.</p>
          </div>
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <BarChart data={topRoutes} layout="vertical" margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" horizontal={false} />
              <XAxis
                type="number"
                tickFormatter={(v: number) => fmtMoney(v)}
                stroke="hsl(var(--muted-foreground))"
                fontSize={11}
                tickLine={false}
                axisLine={{ stroke: 'hsl(var(--border))' }}
              />
              <YAxis type="category" dataKey="label" stroke="hsl(var(--muted-foreground))" fontSize={11} tickLine={false} axisLine={false} width={110} />
              <Tooltip content={<MoneyTooltip />} cursor={{ fill: 'hsl(var(--muted) / 0.4)' }} />
              <Bar dataKey="amount" radius={[0, 4, 4, 0]}>
                {topRoutes.map((entry) => (
                  <Cell key={entry.label} fill={entry.amount >= 0 ? 'hsl(var(--success))' : 'hsl(var(--danger))'} />
                ))}
              </Bar>
            </BarChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  )
}

export default FinanceOverviewCharts
