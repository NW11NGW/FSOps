import { lazy, Suspense, useEffect, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Clock3, DollarSign, Globe, PlaneTakeoff, Radar, RadioTower, Route, ShieldCheck, Users } from 'lucide-react'

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { PageHeader } from '@/components/shared/PageHeader'
import { StatTile } from '@/components/shared/StatTile'
import { WorldDataStatusBanner } from '@/components/shared/WorldDataStatusBanner'
import { useLiveOperations } from '@/hooks/useLiveOperations'
import { useVatsimAtc } from '@/hooks/useVatsimAtc'
import { useVatsimTraffic } from '@/hooks/useVatsimTraffic'
import { useReputationSummary } from '@/hooks/useReputationSummary'
import { useServerClock } from '@/hooks/useServerClock'
import { useSettings } from '@/hooks/useSettings'
import { useWorldDataStatus } from '@/hooks/useWorldDataStatus'
import { AtcControllerList, AtcCountBadge } from '@/components/map/AtcControllerList'
import { VatsimHistoryCard } from '@/components/map/VatsimHistoryCard'
import type { MapBounds } from '@/components/map/atcVisibility'
import { reputationDemandLabel, reputationDrivers, reputationTrendLabel } from '@/lib/reputation'
import { readVatsimTrafficVisible, writeVatsimTrafficVisible } from '@/lib/vatsimTrafficVisibility'
import type { LiveContext } from '@/types/live-context'
import type { ReputationDirection } from '@/types/airline'

// maplibre-gl is the single heaviest dependency in the app (~210 kB gzipped) and the Dashboard is
// the route almost everyone lands on first, so it must not sit on the critical path for the rest
// of the page. Splitting it out lets the clock, KPIs, and reputation card paint immediately while
// the map streams in behind them - the fallback below matches the existing loading skeleton this
// card already used for the liveOps-loading state.
const LiveOpsMap = lazy(() => import('@/components/map/LiveOpsMap').then((m) => ({ default: m.LiveOpsMap })))

const REPUTATION_BADGE_VARIANT: Record<ReputationDirection, NonNullable<BadgeProps['variant']>> = {
  improving: 'success',
  declining: 'danger',
  steady: 'muted',
  new: 'muted',
}

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
  const liveOps = useLiveOperations()
  // The map can show a controller anywhere the user pans, so this asks for the world and the
  // client narrows it to the viewport - see AtcControllerList. Geometry only because this page
  // actually draws the polygons; a map-free consumer must not pay for coordinates it discards.
  const atc = useVatsimAtc({ scope: 'all', geometry: true })
  // Null until the lazy-loaded map mounts and reports its first view, which correctly leaves the
  // list showing the server's own network scoping in the meantime rather than nothing.
  const [atcViewport, setAtcViewport] = useState<MapBounds | null>(null)
  // G11 - other VATSIM traffic, OFF by default (including for a brand-new user with nothing
  // stored yet - see vatsimTrafficVisibility's own doc for why the read must never quietly
  // default to on) so the map is clean on first load, and remembered per-browser once toggled.
  const [showVatsimTraffic, setShowVatsimTraffic] = useState(readVatsimTrafficVisible)
  useEffect(() => {
    writeVatsimTrafficVisible(showVatsimTraffic)
  }, [showVatsimTraffic])
  const traffic = useVatsimTraffic(showVatsimTraffic)
  const reputation = useReputationSummary()
  const { fmt } = useSettings()

  const summary = airlineSummary.data
  const summaryLoading = airlineSummary.status === 'loading'
  const summaryUnavailable = airlineSummary.status === 'error'

  const reputationTrend = reputation.data ? reputationTrendLabel(reputation.data.direction) : undefined

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

        <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-4 lg:col-span-2">
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
          <StatTile
            label="Reputation"
            value={
              reputation.status === 'ready' && reputation.data
                ? String(Math.round(reputation.data.score))
                : reputation.status === 'error'
                  ? '—'
                  : undefined
            }
            icon={ShieldCheck}
            trend={
              reputationTrend ?? (reputation.status === 'error' ? { direction: 'flat', label: 'Unavailable' } : undefined)
            }
            loading={reputation.status === 'loading'}
          />
        </div>
      </div>

      <Card className="mt-4">
        <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
            <ShieldCheck className="size-4" />
            Reputation
          </CardTitle>
          {reputation.status === 'ready' && reputation.data && reputationTrend && (
            <Badge variant={REPUTATION_BADGE_VARIANT[reputation.data.direction]}>{reputationTrend.label}</Badge>
          )}
        </CardHeader>
        <CardContent>
          {reputation.status === 'loading' && (
            <div className="space-y-3">
              <Skeleton className="h-9 w-16" />
              <Skeleton className="h-4 w-72" />
              <Skeleton className="h-4 w-56" />
            </div>
          )}

          {reputation.status === 'error' && (
            <p className="text-sm text-muted-foreground">Could not load your reputation. Check your connection and try again.</p>
          )}

          {reputation.status === 'none' && (
            <p className="text-sm text-muted-foreground">Create your airline to start building a reputation.</p>
          )}

          {reputation.status === 'ready' && reputation.data && (
            <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
              <div>
                {/* Rounded for display, matching the StatTile above - the underlying score moves in
                 *  fractions of a point per sector, and showing that precision here would claim an
                 *  accuracy the model was never meant to imply. */}
                <p className="text-4xl font-semibold tabular-nums tracking-tight">{Math.round(reputation.data.score)}</p>
                <p className="mt-1 text-sm text-muted-foreground">{reputationDemandLabel(reputation.data.demandMultiplier)}</p>
              </div>
              <ul className="space-y-1.5 text-sm text-muted-foreground sm:max-w-sm">
                {reputationDrivers(reputation.data).map((driver) => (
                  <li key={driver} className="flex gap-2">
                    <span aria-hidden>•</span>
                    <span>{driver}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </CardContent>
      </Card>

      <Card className="mt-4">
        <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
            <Radar className="size-4" />
            Live operations
          </CardTitle>
          <div className="flex items-center gap-2">
            {liveOps.status === 'ready' && liveOps.data && liveOps.data.aircraft.length > 0 && (
              <Badge variant="success">
                {liveOps.data.aircraft.length} airborne
              </Badge>
            )}
            {/* G11: other people's traffic, toggleable and on by default on the dashboard. */}
            <Button
              type="button"
              variant="outline"
              size="sm"
              aria-pressed={showVatsimTraffic}
              onClick={() => setShowVatsimTraffic((v) => !v)}
              className="h-7 px-2 text-xs"
            >
              <RadioTower className="size-3.5" />
              {showVatsimTraffic ? 'Hide VATSIM traffic' : 'Show VATSIM traffic'}
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          {liveOps.status === 'loading' && <Skeleton className="h-[360px] w-full rounded-lg" />}
          {liveOps.status === 'error' && (
            <div className="flex h-[360px] items-center justify-center rounded-lg border border-border bg-surface text-sm text-muted-foreground">
              Could not reach the live hub — retrying…
            </div>
          )}
          {liveOps.status === 'ready' && liveOps.data && (
            <Suspense fallback={<Skeleton className="h-[360px] w-full rounded-lg" />}>
              <LiveOpsMap
                aircraft={liveOps.data.aircraft}
                network={liveOps.data.network}
                atcControllers={atc.status === 'ready' && atc.data?.status === 'ok' ? atc.data.controllers : []}
                atcBoundaries={atc.data?.boundaries ?? null}
                atcUnavailable={atc.status === 'error' || atc.data?.status === 'unavailable'}
                vatsimTraffic={showVatsimTraffic && traffic.status === 'ready' && traffic.data?.status === 'ok' ? traffic.data.pilots : []}
                onViewportChange={setAtcViewport}
                className="h-[360px]"
              />
            </Suspense>
          )}
        </CardContent>
      </Card>

      <VatsimHistoryCard />

      <Card className="mt-4">
        <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
            <RadioTower className="size-4" />
            ATC coverage
          </CardTitle>
          <AtcCountBadge status={atc.status} data={atc.data} viewport={atcViewport} />
        </CardHeader>
        <CardContent>
          {/* Same viewport the map above reports, so this list is always describing the picture
              the user is currently looking at rather than a fixed slice of the world. */}
          <AtcControllerList status={atc.status} data={atc.data} viewport={atcViewport} />
        </CardContent>
      </Card>
    </div>
  )
}
