import { lazy, Suspense } from 'react'
import { useOutletContext } from 'react-router-dom'
import { BarChart3, Building2, Clock, DollarSign, Gauge, Plane } from 'lucide-react'

import { ChartSkeleton } from '@/components/stats/ChartSkeleton'
import { ExportCsvButton } from '@/components/stats/ExportCsvButton'
import { FleetUtilisationTable } from '@/components/stats/FleetUtilisationTable'
import { PeriodSelector } from '@/components/stats/PeriodSelector'
import { PilotLogbookTable } from '@/components/stats/PilotLogbookTable'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { StatTile } from '@/components/shared/StatTile'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useSettings } from '@/hooks/useSettings'
import { useStatsOverview } from '@/hooks/useStatsOverview'
import type { CsvColumn } from '@/lib/csv'
import type { LiveContext } from '@/types/live-context'
import type { StatsPerformancePoint } from '@/types/stats'

// Both charts pull in recharts, so both are lazy-loaded exactly like maplibre-gl is for
// Dashboard/Routes/Fly (see App.tsx) - a player who never opens Stats never downloads a charting
// library at all, and this page's own chunk (already lazy at the route level) stays lean too.
const PerformanceChart = lazy(() => import('@/components/stats/PerformanceChart').then((m) => ({ default: m.PerformanceChart })))
const FinanceOverviewCharts = lazy(() => import('@/components/stats/FinanceOverviewCharts').then((m) => ({ default: m.FinanceOverviewCharts })))

const PERFORMANCE_CSV_COLUMNS: CsvColumn<StatsPerformancePoint>[] = [
  { key: 'dateUtc', header: 'Date' },
  { key: 'sectorsFlown', header: 'Sectors flown' },
  { key: 'onTimePercent', header: 'On time %' },
  { key: 'loadFactorPercent', header: 'Load factor %' },
]

function average(values: number[]): number | null {
  return values.length === 0 ? null : values.reduce((sum, v) => sum + v, 0) / values.length
}

export function Stats() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const overview = useStatsOverview()
  const { fmt } = useSettings()

  if (airlineSummary.status === 'loading') {
    return (
      <div className="space-y-4">
        <PageHeader title="Stats" description="Performance trends across routes, pilots, and fleet." />
      </div>
    )
  }

  if (airlineSummary.status === 'error' || !airlineSummary.data) {
    return (
      <div className="space-y-4">
        <PageHeader title="Stats" description="Performance trends across routes, pilots, and fleet." />
        <EmptyState icon={Building2} title="No airline yet" description="Set up your airline before there is anything to show here." />
      </div>
    )
  }

  const loading = overview.status === 'loading'
  const totalSectors = overview.performance.reduce((sum, p) => sum + p.sectorsFlown, 0)
  const avgOnTime = average(overview.performance.map((p) => p.onTimePercent).filter((v): v is number => v !== null))
  const avgLoadFactor = average(overview.performance.map((p) => p.loadFactorPercent).filter((v): v is number => v !== null))
  const avgUtilisation = average(overview.fleet.map((a) => a.utilisationPercent))

  const hasPerformanceData = overview.performance.length > 0
  const hasFinanceData = overview.routes.length > 0 || (overview.costs !== null && (overview.costs.revenue.total !== 0 || overview.costs.fixed.total !== 0 || overview.costs.variable.total !== 0))

  return (
    <div className="space-y-4">
      <PageHeader
        title="Stats"
        description="Performance trends across routes, pilots, and fleet."
        actions={<PeriodSelector value={overview.periodDays} onChange={overview.setPeriodDays} />}
      />

      {overview.status === 'error' && (
        <div className="rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          Could not load your stats. Check your connection and try again.
        </div>
      )}

      {/* The "not a calendar month" caveat only says anything at 30 days, where the window is easy
          to mistake for one. At 7 or 90 it reads as a non-sequitur, so it is left out. */}
      <p className="text-xs text-muted-foreground">
        Showing the trailing {overview.periodDays} days
        {overview.periodDays === 30 ? ', not a calendar month' : ''} - the app bills on a rolling cycle, and every
        figure below is measured from completed flights over exactly this window.
      </p>

      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-4">
        <StatTile label="Sectors flown" value={loading ? undefined : String(totalSectors)} icon={Plane} loading={loading} />
        <StatTile
          label="Avg on-time"
          value={loading ? undefined : avgOnTime === null ? 'Not measured' : `${avgOnTime.toFixed(0)}%`}
          icon={Clock}
          loading={loading}
        />
        <StatTile
          label="Avg load factor"
          value={loading ? undefined : avgLoadFactor === null ? 'Not measured' : `${avgLoadFactor.toFixed(0)}%`}
          icon={Gauge}
          loading={loading}
        />
        <StatTile
          label="Fleet utilisation"
          value={loading ? undefined : avgUtilisation === null ? 'No aircraft' : `${avgUtilisation.toFixed(0)}%`}
          icon={BarChart3}
          loading={loading}
        />
      </div>

      <Tabs defaultValue="performance">
        <TabsList>
          <TabsTrigger value="performance">Performance</TabsTrigger>
          <TabsTrigger value="finance">Finance</TabsTrigger>
          <TabsTrigger value="fleet">Fleet</TabsTrigger>
          <TabsTrigger value="pilots">Pilots</TabsTrigger>
        </TabsList>

        <TabsContent value="performance">
          <Card>
            <CardHeader className="flex-row items-start justify-between gap-4 space-y-0 pb-3">
              <div>
                <CardTitle className="flex items-center gap-2 text-base">
                  <Clock className="size-4 text-muted-foreground" />
                  On-time performance &amp; load factor
                </CardTitle>
                <p className="text-xs text-muted-foreground">One point per day that had at least one completed sector - a quiet day is simply absent, never a fabricated 0%.</p>
              </div>
              <ExportCsvButton filename={`fsops-performance-last-${overview.periodDays}-days.csv`} rows={overview.performance} columns={PERFORMANCE_CSV_COLUMNS} />
            </CardHeader>
            <CardContent>
              {loading && <ChartSkeleton height={280} />}
              {!loading && !hasPerformanceData && (
                <EmptyState
                  icon={Clock}
                  title="No completed flights yet"
                  description="Fly a sector yourself, or let a virtual pilot fly one, and on-time performance and load factor will start charting here."
                />
              )}
              {!loading && hasPerformanceData && (
                <Suspense fallback={<ChartSkeleton height={280} />}>
                  <PerformanceChart points={overview.performance} />
                </Suspense>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="finance">
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="flex items-center gap-2 text-base">
                <DollarSign className="size-4 text-muted-foreground" />
                Revenue &amp; cost
              </CardTitle>
              <p className="text-xs text-muted-foreground">
                Reuses the same figures as the Finances page's cost breakdown and per-route P&amp;L - never recomputed here, so the two pages can never
                disagree.
              </p>
            </CardHeader>
            <CardContent>
              {loading && <ChartSkeleton height={240} />}
              {!loading && !hasFinanceData && (
                <EmptyState icon={DollarSign} title="No revenue or costs posted yet" description="Once flights start posting to the ledger, revenue, costs, and route P&L will chart here." />
              )}
              {!loading && hasFinanceData && (
                <Suspense fallback={<ChartSkeleton height={240} />}>
                  <FinanceOverviewCharts costs={overview.costs} routes={overview.routes} fmtMoney={fmt.money} />
                </Suspense>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="fleet">
          <FleetUtilisationTable status={overview.status} aircraft={overview.fleet} periodDays={overview.periodDays} />
        </TabsContent>

        <TabsContent value="pilots">
          <PilotLogbookTable status={overview.status} pilots={overview.pilots} periodDays={overview.periodDays} />
        </TabsContent>
      </Tabs>
    </div>
  )
}
