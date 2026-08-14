import { useMemo, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Banknote, Building2, Clock3, NotebookPen, SearchX } from 'lucide-react'

import { FlightTrackCard } from '@/components/flight/FlightTrackCard'
import { ReportCard } from '@/components/flight/ReportCard'
import { LogbookFilterBar } from '@/components/logbook/LogbookFilterBar'
import { LogbookTable } from '@/components/logbook/LogbookTable'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { StatTile } from '@/components/shared/StatTile'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { useFlightDetail } from '@/hooks/useFlightDetail'
import { useLogbook } from '@/hooks/useLogbook'
import { useRoutes } from '@/hooks/useRoutes'
import { useSettings } from '@/hooks/useSettings'
import {
  DEFAULT_LOGBOOK_FILTERS,
  filterSectors,
  sortSectors,
  totalsFor,
  type LogbookFilters,
  type LogbookSortKey,
  type SortDirection,
} from '@/lib/logbook'
import type { LogbookSector } from '@/types/flight'
import type { LiveContext } from '@/types/live-context'
import type { RouteSummary } from '@/types/route'

/**
 * The flight logbook: every sector actually flown, browsable.
 *
 * Its own page rather than a tab, because it is a destination in its own right - the place a pilot
 * goes to look something up afterwards, not a statistic. Everything here comes from
 * GET /flights/logbook, already joined server-side; sorting and filtering happen in the browser, so
 * neither ever costs a round trip.
 */
export function Logbook() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const { fmt } = useSettings()
  const logbook = useLogbook()
  const routesQuery = useRoutes()

  const [filters, setFilters] = useState<LogbookFilters>(DEFAULT_LOGBOOK_FILTERS)
  const [sortKey, setSortKey] = useState<LogbookSortKey>('date')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')
  const [openSector, setOpenSector] = useState<LogbookSector | null>(null)

  const detail = useFlightDetail(openSector?.flightId ?? null)

  const routesById = useMemo(
    () => Object.fromEntries(routesQuery.routes.map((route) => [route.id, route])) as Record<string, RouteSummary>,
    [routesQuery.routes],
  )

  const visible = useMemo(
    () => sortSectors(filterSectors(logbook.sectors, filters), sortKey, sortDirection),
    [logbook.sectors, filters, sortKey, sortDirection],
  )
  const totals = useMemo(() => totalsFor(visible), [visible])

  function handleSort(key: LogbookSortKey) {
    if (key === sortKey) {
      setSortDirection((current) => (current === 'asc' ? 'desc' : 'asc'))
      return
    }
    setSortKey(key)
    // Newest and biggest first is what a reader expects from a fresh column; only text columns
    // read better the other way round.
    setSortDirection(key === 'route' || key === 'aircraft' ? 'asc' : 'desc')
  }

  const airlineIcaoCode = airlineSummary.data?.airline.icaoCode ?? null
  const openRoute = openSector ? routesById[openSector.routeId] : undefined

  if (airlineSummary.status === 'error' || (airlineSummary.status === 'ready' && !airlineSummary.data)) {
    return (
      <div className="space-y-4">
        <PageHeader title="Logbook" description="Every sector you have flown." />
        <EmptyState icon={Building2} title="No airline yet" description="Set up your airline before there is anything to log." />
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <PageHeader
        title="Logbook"
        description="Every sector actually flown — how long it took against plan, how it landed, and what it earned."
      />

      {logbook.status === 'error' && (
        <div className="rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          Could not load your logbook. Check your connection and try again.
        </div>
      )}

      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-4">
        <StatTile
          label={filters === DEFAULT_LOGBOOK_FILTERS ? 'Sectors flown' : 'Sectors shown'}
          value={logbook.status === 'loading' ? undefined : String(totals.sectors)}
          icon={NotebookPen}
          loading={logbook.status === 'loading'}
        />
        <StatTile
          label="Block hours"
          value={logbook.status === 'loading' ? undefined : `${(totals.blockMinutes / 60).toFixed(1)}h`}
          icon={Clock3}
          loading={logbook.status === 'loading'}
        />
        <StatTile
          label="Net earned"
          value={logbook.status === 'loading' ? undefined : `${totals.net < 0 ? '-' : ''}${fmt.money(Math.abs(totals.net))}`}
          icon={Banknote}
          loading={logbook.status === 'loading'}
        />
      </div>

      {totals.unmeasuredBlockSectors > 0 && (
        <p className="text-xs text-muted-foreground">
          Block hours are a floor, not a total: {totals.unmeasuredBlockSectors} of the sectors shown could not be measured — either
          they never recorded both an out and an in time, or the simulator ran faster than real time, which makes elapsed wall time
          meaningless. Those sectors contribute nothing to the figure rather than an invented estimate.
        </p>
      )}

      <Card>
        <CardContent className="space-y-4 p-4">
          <LogbookFilterBar filters={filters} onChange={setFilters} matching={visible.length} loaded={logbook.sectors.length} />

          {logbook.status === 'loading' && (
            <div className="space-y-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          )}

          {logbook.status === 'ready' && logbook.sectors.length === 0 && (
            <EmptyState
              icon={NotebookPen}
              title="No sectors flown yet"
              description="Fly a sector yourself, or let one of your pilots fly one, and it appears here with its block time, landing and what it earned."
            />
          )}

          {logbook.status === 'ready' && logbook.sectors.length > 0 && visible.length === 0 && (
            <EmptyState
              icon={SearchX}
              title="Nothing matches those filters"
              description="No sector in your logbook matches what you have narrowed it down to. Clear a filter to see more."
            />
          )}

          {logbook.status === 'ready' && visible.length > 0 && (
            <>
              <div className="overflow-x-auto">
                <LogbookTable
                  sectors={visible}
                  sortKey={sortKey}
                  sortDirection={sortDirection}
                  onSort={handleSort}
                  onOpen={setOpenSector}
                  airlineIcaoCode={airlineIcaoCode}
                />
              </div>
              {logbook.totalSectors > logbook.sectors.length && (
                <p className="text-xs text-muted-foreground">
                  Showing your most recent {logbook.sectors.length} sectors of {logbook.totalSectors} flown.
                </p>
              )}
            </>
          )}
        </CardContent>
      </Card>

      <Dialog open={openSector !== null} onOpenChange={(open) => !open && setOpenSector(null)}>
        <DialogContent className="max-h-[85vh] max-w-3xl overflow-y-auto">
          <DialogHeader>
            <DialogTitle>
              {openSector ? `${openSector.departureIcao} → ${openSector.arrivalIcao}` : 'Flight report'}
            </DialogTitle>
          </DialogHeader>
          {detail.status === 'loading' && <Skeleton className="h-64 w-full" />}
          {detail.status === 'error' && <p className="text-sm text-danger">{detail.errorMessage}</p>}
          {detail.status === 'ready' && detail.data && openSector && (
            <ReportCard
              detail={detail.data}
              route={
                openRoute
                  ? {
                      departureIcao: openRoute.departureIcao,
                      departureName: openRoute.departureName,
                      arrivalIcao: openRoute.arrivalIcao,
                      arrivalName: openRoute.arrivalName,
                      flightNumber: openRoute.flightNumber,
                    }
                  : {
                      // The route may have been deleted since the sector flew - the logbook row
                      // still carries the ICAOs it was flown between, so the record stays readable
                      // instead of collapsing to "Flight report".
                      departureIcao: openSector.departureIcao,
                      departureName: null,
                      arrivalIcao: openSector.arrivalIcao,
                      arrivalName: null,
                      flightNumber: openSector.flightNumber,
                    }
              }
              airlineIcaoCode={airlineIcaoCode}
              track={
                <FlightTrackCard
                  flightId={openSector.flightId}
                  departureIcao={openSector.departureIcao || null}
                  arrivalIcao={openSector.arrivalIcao || null}
                />
              }
            />
          )}
        </DialogContent>
      </Dialog>
    </div>
  )
}
