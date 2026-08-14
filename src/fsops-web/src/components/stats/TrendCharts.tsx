import {
  Area,
  AreaChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'

import type { StatsTrendPoint } from '@/types/stats'

interface TrendChartsProps {
  points: StatsTrendPoint[]
  currentReputation: number | null
  reputationRecordedDays: number
  fmtMoney: (amount: number) => string
}

function formatAxisDate(dateUtc: string): string {
  const date = new Date(`${dateUtc}T00:00:00Z`)
  if (Number.isNaN(date.getTime())) return dateUtc
  return date.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', timeZone: 'UTC' })
}

interface TooltipEntry {
  color: string
  name: string
  value: number | null
  dataKey: string
}

function makeTooltip(renderValue: (entry: TooltipEntry) => string) {
  return function TrendTooltip({ active, payload, label }: { active?: boolean; payload?: TooltipEntry[]; label?: string }) {
    if (!active || !payload || payload.length === 0) return null
    // A null value means "not measured that day". Dropping the row entirely is deliberate: showing
    // "On time: 0%" for a day nothing flew would be a fabricated figure, and showing a dash invites
    // it to be read as a real zero anyway.
    const measured = payload.filter((entry) => entry.value !== null && entry.value !== undefined)
    return (
      <div className="rounded-md border border-border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-elevation-2">
        <p className="mb-1 font-medium">{label ? formatAxisDate(label) : ''}</p>
        {measured.length === 0 ? (
          <p className="text-muted-foreground">Nothing measured this day</p>
        ) : (
          measured.map((entry) => (
            <p key={entry.dataKey} style={{ color: entry.color }}>
              {entry.name}: {renderValue(entry)}
            </p>
          ))
        )}
      </div>
    )
  }
}

/**
 * Direction of travel, over time: cash on one chart, standing on the other.
 *
 * Deliberately does NOT re-plot on-time and load factor - PerformanceChart alongside already owns
 * those, and drawing them twice on one page would invite the two to be compared as though they were
 * different measurements. Every series here comes from GET /stats/trends; see StatsTrendPoint for
 * where each figure is derived from.
 *
 * Extends the existing chart vocabulary rather than inventing a second one: the same recharts
 * primitives, the same token-driven colours, the same "a day with nothing to say is absent, never a
 * fabricated zero" rule the performance chart already follows.
 */
export function TrendCharts({ points, currentReputation, reputationRecordedDays, fmtMoney }: TrendChartsProps) {
  const MoneyTooltip = makeTooltip((entry) => fmtMoney(entry.value ?? 0))
  const ScoreTooltip = makeTooltip((entry) => String(Math.round(entry.value ?? 0)))

  const hasReputationSeries = points.some((p) => p.reputation !== null || p.reputationPressure !== null)

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <div>
        <p className="mb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">Cash balance</p>
        <p className="mb-2 text-xs text-muted-foreground">
          The balance at the end of each day, summed from the ledger itself — so this line is the same money the Finances page
          shows, on the day it moved. A day with no flying still has a point: cash does not stop existing, it just does not move.
        </p>
        <ResponsiveContainer width="100%" height={240}>
          <AreaChart data={points} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
            <defs>
              <linearGradient id="fsops-cash-fill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="hsl(var(--chart-5))" stopOpacity={0.35} />
                <stop offset="100%" stopColor="hsl(var(--chart-5))" stopOpacity={0.02} />
              </linearGradient>
            </defs>
            <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" vertical={false} />
            <XAxis
              dataKey="dateUtc"
              tickFormatter={formatAxisDate}
              stroke="hsl(var(--muted-foreground))"
              fontSize={11}
              tickLine={false}
              axisLine={{ stroke: 'hsl(var(--border))' }}
              minTickGap={24}
            />
            <YAxis
              tickFormatter={(v: number) => fmtMoney(v)}
              stroke="hsl(var(--muted-foreground))"
              fontSize={11}
              tickLine={false}
              axisLine={{ stroke: 'hsl(var(--border))' }}
              width={84}
            />
            <Tooltip content={<MoneyTooltip />} cursor={{ stroke: 'hsl(var(--border))' }} />
            {/* Zero is the line that matters most on this chart - crossing it is insolvency, and it
                must be visible even when the whole series sits above or below it. */}
            <ReferenceLine y={0} stroke="hsl(var(--danger))" strokeDasharray="4 4" strokeOpacity={0.7} />
            <Area
              type="monotone"
              dataKey="cashBalance"
              name="Cash"
              stroke="hsl(var(--chart-5))"
              strokeWidth={2}
              fill="url(#fsops-cash-fill)"
              dot={false}
              isAnimationActive={false}
            />
          </AreaChart>
        </ResponsiveContainer>
      </div>

      <div>
        <p className="mb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">Reputation</p>
        <p className="mb-2 text-xs text-muted-foreground">
          <span className="font-medium text-foreground">Recorded</span> is your actual score, written down once a day from the day
          this started being kept — it is a real reading, so days FSOps was not running are simply missing rather than filled in.{' '}
          <span className="font-medium text-foreground">Pressure</span> is not your reputation: it is the standard each day&rsquo;s
          flying was pulling it <em>toward</em>. Above your current score means it is being pulled up, below means down. It is the
          same figure the dashboard&rsquo;s reputation card uses to say &ldquo;improving&rdquo; or &ldquo;declining&rdquo;, and it
          works right back through history, which recorded scores cannot.
        </p>
        {!hasReputationSeries ? (
          <div className="flex h-[240px] flex-col items-center justify-center gap-1 rounded-md border border-dashed border-border px-6 text-center">
            <p className="text-sm font-medium">Nothing to plot yet</p>
            <p className="max-w-[280px] text-xs text-muted-foreground">
              Fly a sector and its effect on your reputation appears here. Your recorded score starts building up a day at a time
              from now on.
            </p>
          </div>
        ) : (
          <ResponsiveContainer width="100%" height={240}>
            <LineChart data={points} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" vertical={false} />
              <XAxis
                dataKey="dateUtc"
                tickFormatter={formatAxisDate}
                stroke="hsl(var(--muted-foreground))"
                fontSize={11}
                tickLine={false}
                axisLine={{ stroke: 'hsl(var(--border))' }}
                minTickGap={24}
              />
              <YAxis
                domain={[0, 100]}
                stroke="hsl(var(--muted-foreground))"
                fontSize={11}
                tickLine={false}
                axisLine={{ stroke: 'hsl(var(--border))' }}
                width={36}
              />
              <Tooltip content={<ScoreTooltip />} />
              <Legend wrapperStyle={{ fontSize: 11, color: 'hsl(var(--muted-foreground))' }} />
              {currentReputation !== null && (
                <ReferenceLine
                  y={currentReputation}
                  stroke="hsl(var(--accent))"
                  strokeDasharray="4 4"
                  label={{ value: `Now ${Math.round(currentReputation)}`, position: 'insideTopRight', fontSize: 11, fill: 'hsl(var(--accent))' }}
                />
              )}
              {/* connectNulls is deliberately OFF for the recorded series: a day with no reading is a
                  day FSOps never observed, and bridging the gap would draw a score it never saw.
                  Dots make an isolated reading visible - with no dot, a single-point series with a
                  broken line renders as nothing at all. */}
              <Line
                type="monotone"
                dataKey="reputation"
                name="Recorded"
                stroke="hsl(var(--chart-4))"
                strokeWidth={2}
                dot={{ r: 2 }}
                connectNulls={false}
                isAnimationActive={false}
              />
              {/* The pressure series DOES connect across gaps: a day with no flying has no pressure
                  to report, but the trend either side of it is one continuous story about how the
                  flying is going, not two. Dashed so it can never be mistaken for the real score. */}
              <Line
                type="monotone"
                dataKey="reputationPressure"
                name="Pressure"
                stroke="hsl(var(--chart-1))"
                strokeWidth={2}
                strokeDasharray="5 4"
                dot={false}
                connectNulls
                isAnimationActive={false}
              />
            </LineChart>
          </ResponsiveContainer>
        )}
        {reputationRecordedDays === 0 && hasReputationSeries && (
          <p className="mt-2 text-xs text-muted-foreground">
            No recorded scores in this window yet — reputation only started being written down recently, so the dashed pressure
            line is all there is to show for older days.
          </p>
        )}
      </div>
    </div>
  )
}

export default TrendCharts
