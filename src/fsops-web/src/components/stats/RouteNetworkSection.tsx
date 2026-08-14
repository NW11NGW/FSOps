import { lazy, Suspense, useMemo, useState } from 'react'
import { Map as MapIcon, Route as RouteIcon } from 'lucide-react'

import type { NetworkArc, NetworkMapAirport } from '@/components/map/NetworkMap'
import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useAirportCoordinates } from '@/hooks/useAirportCoordinates'
import { useSettings } from '@/hooks/useSettings'
import { sampleGreatCirclePath } from '@/lib/geo'
import {
  BAND_LABEL,
  BAND_TEXT_CLASS,
  bandFor,
  buildNetworkLinks,
  describeBand,
  networkAverageProfitPerSector,
  type NetworkBand,
  type NetworkLink,
} from '@/lib/networkHealth'
import { cn } from '@/lib/utils'
import type { FinanceRoute } from '@/types/finance'
import type { RouteSummary } from '@/types/route'

// maplibre-gl is the app's heaviest dependency and must only ever be reachable through a dynamic
// import (see vite.config.ts's manualChunks note) - so the map is lazy here exactly as it is for
// Dashboard, Routes and the live flight view.
const NetworkMap = lazy(() => import('@/components/map/NetworkMap').then((m) => ({ default: m.NetworkMap })))

interface RouteNetworkSectionProps {
  routes: RouteSummary[]
  routesLoading: boolean
  financeRoutes: FinanceRoute[]
  homeAirportIcao: string | null
  periodDays: number
  loading: boolean
}

const BAND_ORDER: NetworkBand[] = ['above-average', 'below-average', 'losing', 'not-flown']

/** A coloured dot plus its label, for the legend and for each row in the list. */
function BandDot({ band, className }: { band: NetworkBand; className?: string }) {
  const dotClass: Record<NetworkBand, string> = {
    'not-flown': 'bg-muted-foreground',
    losing: 'bg-danger',
    'below-average': 'bg-warning',
    'above-average': 'bg-success',
  }
  return <span aria-hidden className={cn('inline-block size-2.5 shrink-0 rounded-full', dotClass[band], className)} />
}

function LinkFigures({ link, fmtMoney }: { link: NetworkLink; fmtMoney: (amount: number) => string }) {
  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-3">
      <div>
        <dt className="text-xs text-muted-foreground">Sectors flown</dt>
        <dd className="tabular-nums">{link.sectorsFlown}</dd>
      </div>
      <div>
        <dt className="text-xs text-muted-foreground">Profit per sector</dt>
        <dd className={cn('tabular-nums font-medium', link.profitPerSector === null ? '' : link.profitPerSector < 0 ? 'text-danger' : 'text-success')}>
          {link.profitPerSector === null
            ? '—'
            : `${link.profitPerSector < 0 ? '-' : ''}${fmtMoney(Math.abs(link.profitPerSector))}`}
        </dd>
      </div>
      <div>
        <dt className="text-xs text-muted-foreground">Margin</dt>
        <dd className="tabular-nums">{link.marginPercent === null ? '—' : `${link.marginPercent.toFixed(0)}%`}</dd>
      </div>
      <div>
        <dt className="text-xs text-muted-foreground">Revenue</dt>
        <dd className="tabular-nums">{fmtMoney(link.revenue)}</dd>
      </div>
      <div>
        <dt className="text-xs text-muted-foreground">Cost</dt>
        <dd className="tabular-nums">{fmtMoney(link.cost)}</dd>
      </div>
      <div>
        <dt className="text-xs text-muted-foreground">Load factor</dt>
        <dd className="tabular-nums">{link.loadFactorPercent === null ? 'Not measured' : `${link.loadFactorPercent.toFixed(0)}%`}</dd>
      </div>
    </dl>
  )
}

/**
 * The whole network on one map, every city pair coloured by how it is actually doing, with the
 * numbers behind the colour readable beside it.
 *
 * The two directions of a city pair are drawn as one arc - that is how routes are created in this
 * app, and two arcs between the same two airports would sit exactly on top of each other - but each
 * direction's own figures are still listed, because a route that earns outbound and loses on the
 * way back is exactly the sort of thing this screen exists to reveal.
 *
 * Every figure here is the SAME per-route P&L the Finances page shows (GET /finance/routes), not a
 * second calculation of it; the geometry is the same client-side great-circle sampling the Routes
 * page already draws with. Nothing about a route's economics is computed here.
 */
export function RouteNetworkSection({
  routes,
  routesLoading,
  financeRoutes,
  homeAirportIcao,
  periodDays,
  loading,
}: RouteNetworkSectionProps) {
  const { fmt } = useSettings()
  const [selectedPairKey, setSelectedPairKey] = useState<string | null>(null)

  const links = useMemo(() => buildNetworkLinks(routes, financeRoutes), [routes, financeRoutes])
  const networkAverage = useMemo(() => networkAverageProfitPerSector(links), [links])

  const neededIcaos = useMemo(() => {
    const icaos = new Set<string>()
    if (homeAirportIcao) icaos.add(homeAirportIcao)
    for (const link of links) {
      icaos.add(link.fromIcao)
      icaos.add(link.toIcao)
    }
    return Array.from(icaos)
  }, [links, homeAirportIcao])

  const coordsByIcao = useAirportCoordinates(neededIcaos)

  const arcs = useMemo<NetworkArc[]>(() => {
    const result: NetworkArc[] = []
    for (const link of links) {
      const from = coordsByIcao[link.fromIcao]
      const to = coordsByIcao[link.toIcao]
      if (!from || !to) continue
      result.push({
        pairKey: link.pairKey,
        path: sampleGreatCirclePath(from.latitude, from.longitude, to.latitude, to.longitude),
        band: bandFor(link, networkAverage),
      })
    }
    return result
  }, [links, coordsByIcao, networkAverage])

  const airports = useMemo<NetworkMapAirport[]>(() => {
    const byIcao = new Map<string, NetworkMapAirport>()
    for (const icao of neededIcaos) {
      const airport = coordsByIcao[icao]
      if (airport) byIcao.set(icao, { icao, latitude: airport.latitude, longitude: airport.longitude })
    }
    return Array.from(byIcao.values())
  }, [neededIcaos, coordsByIcao])

  const selectedLink = links.find((link) => link.pairKey === selectedPairKey) ?? null

  // Worst per-sector first among the routes that flew, so the thing most worth acting on is at the
  // top - then the routes that did not fly, which are a different kind of question entirely.
  const rankedLinks = useMemo(() => {
    const flown = links.filter((link) => link.profitPerSector !== null).sort((a, b) => a.profitPerSector! - b.profitPerSector!)
    const unflown = links.filter((link) => link.profitPerSector === null)
    return [...flown, ...unflown]
  }, [links])

  const busy = loading || routesLoading

  if (busy) {
    return (
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="flex items-center gap-2 text-base">
            <MapIcon className="size-4 text-muted-foreground" />
            Route network
          </CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(280px,380px)]">
          <Skeleton className="h-[420px] w-full rounded-lg" />
          <Skeleton className="h-[420px] w-full rounded-lg" />
        </CardContent>
      </Card>
    )
  }

  if (links.length === 0) {
    return (
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="flex items-center gap-2 text-base">
            <MapIcon className="size-4 text-muted-foreground" />
            Route network
          </CardTitle>
        </CardHeader>
        <CardContent>
          <EmptyState
            icon={RouteIcon}
            title="No routes yet"
            description="Build a route on the Routes page and your whole network appears here, coloured by how well each one is doing."
          />
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <MapIcon className="size-4 text-muted-foreground" />
          Route network
        </CardTitle>
        <p className="text-xs text-muted-foreground">
          Every route you fly, coloured by <span className="font-medium text-foreground">profit per sector</span> over the last{' '}
          {periodDays} days — what one more flight on that route adds to the bank. That is the comparison worth making, because
          aircraft time is what you are actually spending: a route earning twice as much per sector is worth twice as much of it,
          however thin its margin looks. Each line is a city pair, both directions together; the figures for each direction are
          listed when you pick one.
        </p>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(280px,380px)] lg:items-start">
          <div className="space-y-3">
            <Suspense fallback={<Skeleton className="h-[320px] w-full rounded-lg sm:h-[420px]" />}>
              <NetworkMap
                arcs={arcs}
                airports={airports}
                homeAirportIcao={homeAirportIcao}
                selectedPairKey={selectedPairKey}
                onSelectPair={setSelectedPairKey}
                className="h-[320px] sm:h-[420px]"
              />
            </Suspense>

            <div className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-md border border-border bg-surface px-3 py-2 text-xs">
              {BAND_ORDER.map((band) => (
                <span key={band} className="flex items-center gap-1.5">
                  <BandDot band={band} />
                  <span className={BAND_TEXT_CLASS[band]}>{BAND_LABEL[band]}</span>
                </span>
              ))}
              <span className="text-muted-foreground">
                {networkAverage === null
                  ? 'Nothing flown in this window, so there is no average to compare against yet.'
                  : `"Average" is your own network average of ${networkAverage < 0 ? `a loss of ${fmt.money(Math.abs(networkAverage))}` : fmt.money(networkAverage)} per sector — total profit over total sectors flown.`}
              </span>
            </div>
          </div>

          <div className="space-y-3">
            {selectedLink ? (
              <div className="space-y-3 rounded-lg border border-border bg-surface p-4">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <p className="font-mono text-base font-semibold">
                      {selectedLink.fromIcao} ↔ {selectedLink.toIcao}
                    </p>
                    <p className="text-xs text-muted-foreground">{Math.round(selectedLink.distanceNm).toLocaleString('en-US')} nm each way</p>
                  </div>
                  <Badge variant="outline" className="gap-1.5">
                    <BandDot band={bandFor(selectedLink, networkAverage)} />
                    {BAND_LABEL[bandFor(selectedLink, networkAverage)]}
                  </Badge>
                </div>

                <p className="text-sm text-muted-foreground">{describeBand(selectedLink, networkAverage, fmt.money)}</p>

                <LinkFigures link={selectedLink} fmtMoney={fmt.money} />

                <div className="space-y-1 border-t border-border pt-3">
                  <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">By direction</p>
                  {selectedLink.legs.map((leg) => (
                    <div key={leg.routeId} className="flex items-center justify-between gap-3 text-sm">
                      <span className="font-mono text-xs">
                        {leg.departureIcao} → {leg.arrivalIcao}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        {leg.sectorsFlown === 0 ? (
                          'not flown'
                        ) : (
                          <>
                            {leg.sectorsFlown} {leg.sectorsFlown === 1 ? 'sector' : 'sectors'},{' '}
                            <span className={leg.profit < 0 ? 'text-danger' : 'text-success'}>
                              {leg.profit < 0 ? '-' : ''}
                              {fmt.money(Math.abs(leg.profit))}
                            </span>
                          </>
                        )}
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            ) : (
              <div className="rounded-lg border border-dashed border-border p-4 text-sm text-muted-foreground">
                Pick a line on the map, or a route below, to see its revenue, cost, load factor and how each direction did.
              </div>
            )}

            <div className="max-h-[300px] overflow-y-auto rounded-lg border border-border scrollbar-thin">
              <ul className="divide-y divide-border">
                {rankedLinks.map((link) => {
                  const band = bandFor(link, networkAverage)
                  const selected = link.pairKey === selectedPairKey
                  return (
                    <li key={link.pairKey}>
                      <button
                        type="button"
                        onClick={() => setSelectedPairKey(selected ? null : link.pairKey)}
                        aria-pressed={selected}
                        className={cn(
                          'flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-muted',
                          selected && 'bg-accent/10',
                        )}
                      >
                        <span className="flex min-w-0 items-center gap-2">
                          <BandDot band={band} />
                          <span className="truncate font-mono text-xs">
                            {link.fromIcao} ↔ {link.toIcao}
                          </span>
                        </span>
                        <span className={cn('shrink-0 tabular-nums text-xs', BAND_TEXT_CLASS[band])}>
                          {link.profitPerSector === null
                            ? 'not flown'
                            : `${link.profitPerSector < 0 ? '-' : ''}${fmt.money(Math.abs(link.profitPerSector))}/sector`}
                        </span>
                      </button>
                    </li>
                  )
                })}
              </ul>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}
