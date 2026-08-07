import { useOutletContext } from 'react-router-dom'
import { Clock3, DollarSign, Globe, PlaneTakeoff, Route, Users } from 'lucide-react'

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { PageHeader } from '@/components/shared/PageHeader'
import { StatTile } from '@/components/shared/StatTile'
import { WorldDataStatusBanner } from '@/components/shared/WorldDataStatusBanner'
import { useServerClock } from '@/hooks/useServerClock'
import { useSettings } from '@/hooks/useSettings'
import { useWorldDataStatus } from '@/hooks/useWorldDataStatus'
import type { LiveContext } from '@/types/live-context'

const CLOCK_FORMATTER = new Intl.DateTimeFormat('en-US', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
  timeZone: 'UTC',
})

const DATE_FORMATTER = new Intl.DateTimeFormat('en-US', {
  weekday: 'long',
  year: 'numeric',
  month: 'long',
  day: 'numeric',
  timeZone: 'UTC',
})

export function Dashboard() {
  const { status, heartbeat, airlineSummary } = useOutletContext<LiveContext>()
  const serverNow = useServerClock(heartbeat)
  const worldData = useWorldDataStatus()
  const { fmt } = useSettings()

  const summary = airlineSummary.data
  const summaryLoading = airlineSummary.status === 'loading'
  const summaryUnavailable = airlineSummary.status === 'error'

  const airportsValue =
    worldData.status === 'ready' && worldData.data
      ? worldData.data.airportCount.toLocaleString()
      : worldData.status === 'error'
        ? '—'
        : undefined

  const airportsTrend =
    worldData.status === 'ready' && worldData.data
      ? worldData.data.importInProgress
        ? { direction: 'flat' as const, label: `Importing — ${Math.round(worldData.data.progressPercent)}%` }
        : worldData.data.seeded
          ? { direction: 'flat' as const, label: `${worldData.data.runwayCount.toLocaleString()} runways` }
          : { direction: 'flat' as const, label: 'Not seeded yet' }
      : worldData.status === 'error'
        ? { direction: 'flat' as const, label: 'Status unavailable' }
        : undefined

  return (
    <div>
      <PageHeader
        title="Dashboard"
        description="Your airline at a glance — live status, KPIs, and what needs attention."
      />

      <WorldDataStatusBanner status={worldData.status} data={worldData.data} />

      <div className="grid gap-4 lg:grid-cols-3">
        <Card className="lg:col-span-1">
          <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
              <Clock3 className="size-4" />
              Sim clock (UTC)
            </CardTitle>
            <Badge variant={status === 'connected' ? 'success' : 'muted'}>
              {status === 'connected' ? 'Live' : 'Waiting for hub'}
            </Badge>
          </CardHeader>
          <CardContent>
            {serverNow ? (
              <>
                <p className="font-mono text-4xl font-semibold tabular-nums tracking-tight">
                  {CLOCK_FORMATTER.format(serverNow)}
                </p>
                <p className="mt-1 text-sm text-muted-foreground">{DATE_FORMATTER.format(serverNow)}</p>
              </>
            ) : (
              <>
                <p className="font-mono text-4xl font-semibold tabular-nums tracking-tight text-muted-foreground">
                  --:--:--
                </p>
                <p className="mt-1 text-sm text-muted-foreground">
                  {status === 'disconnected' ? 'Reconnecting to the live hub…' : 'Waiting for the first heartbeat…'}
                </p>
              </>
            )}
            {heartbeat && (
              <p className="mt-3 text-xs text-muted-foreground">Server v{heartbeat.version}</p>
            )}
          </CardContent>
        </Card>

        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:col-span-2">
          <StatTile
            label="Cash balance"
            value={summary ? fmt.money(summary.cashBalance) : summaryUnavailable ? '—' : undefined}
            icon={DollarSign}
            trend={summaryUnavailable ? { direction: 'flat', label: 'Unavailable' } : undefined}
            loading={summaryLoading}
          />
          <StatTile
            label="Active routes"
            value={summary ? String(summary.routeCount) : summaryUnavailable ? '—' : undefined}
            icon={Route}
            trend={
              summary
                ? summary.routeCount === 0
                  ? { direction: 'flat', label: 'No routes yet' }
                  : undefined
                : summaryUnavailable
                  ? { direction: 'flat', label: 'Unavailable' }
                  : undefined
            }
            loading={summaryLoading}
          />
          <StatTile
            label="Fleet size"
            value={summary ? `${summary.fleetCount} aircraft` : summaryUnavailable ? '—' : undefined}
            icon={PlaneTakeoff}
            trend={
              summary
                ? summary.fleetCount === 0
                  ? { direction: 'flat', label: 'No aircraft yet' }
                  : undefined
                : summaryUnavailable
                  ? { direction: 'flat', label: 'Unavailable' }
                  : undefined
            }
            loading={summaryLoading}
          />
          <StatTile
            label="Pilots"
            value={summary ? String(summary.pilotCount) : summaryUnavailable ? '—' : undefined}
            icon={Users}
            trend={
              summary
                ? summary.pilotCount === 0
                  ? { direction: 'flat', label: 'No pilots yet' }
                  : undefined
                : summaryUnavailable
                  ? { direction: 'flat', label: 'Unavailable' }
                  : undefined
            }
            loading={summaryLoading}
          />
          <StatTile
            label="Airports in database"
            value={airportsValue}
            icon={Globe}
            trend={airportsTrend}
            loading={worldData.status === 'loading'}
          />
        </div>
      </div>
    </div>
  )
}
