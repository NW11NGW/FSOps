import { useCallback, useEffect, useMemo, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { PlaneTakeoff, RotateCcw } from 'lucide-react'
import { toast } from 'sonner'

import { FlightBrief } from '@/components/flight/FlightBrief'
import { FlightHistoryList } from '@/components/flight/FlightHistoryList'
import { FlightTrackCard } from '@/components/flight/FlightTrackCard'
import { FlightPlanImportBanner } from '@/components/flight/FlightPlanImportBanner'
import { LiveFlightView } from '@/components/flight/LiveFlightView'
import { NeedsResolutionPanel } from '@/components/flight/NeedsResolutionPanel'
import { ReportCard } from '@/components/flight/ReportCard'
import { RouteSelector } from '@/components/flight/RouteSelector'
import { pairRouteRows } from '@/components/flight/routeRow'
import type { RouteRow } from '@/components/flight/routeRow'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { useActiveFlight } from '@/hooks/useActiveFlight'
import { useAircraftTypes } from '@/hooks/useAircraftTypes'
import { useAirportCoordinates } from '@/hooks/useAirportCoordinates'
import { useFlightDetail } from '@/hooks/useFlightDetail'
import { useFlightHistory } from '@/hooks/useFlightHistory'
import { useFlightLive } from '@/hooks/useFlightLive'
import { useFlightOptions } from '@/hooks/useFlightOptions'
import { useFlightPlanImport } from '@/hooks/useFlightPlanImport'
import { useFlightPreview } from '@/hooks/useFlightPreview'
import { useRouteBlockTimes } from '@/hooks/useRouteBlockTimes'
import { useRoutes } from '@/hooks/useRoutes'
import { useSimStatus } from '@/hooks/useSimStatus'
import { ApiError, post } from '@/lib/api'
import { sampleGreatCirclePath } from '@/lib/geo'
import type { Flight, FlightDetail, StartFlightResponse } from '@/types/flight'
import type { LiveContext } from '@/types/live-context'
import type { RouteSummary } from '@/types/route'

function describeError(err: unknown, fallback: string): string {
  return err instanceof ApiError ? err.message : fallback
}

export function Fly() {
  const { airlineSummary } = useOutletContext<LiveContext>()

  const routesQuery = useRoutes()
  const routeBlockMinutes = useRouteBlockTimes(routesQuery.routes)
  const optionsQuery = useFlightOptions()
  const aircraftTypesQuery = useAircraftTypes()
  const activeFlight = useActiveFlight()
  const historyQuery = useFlightHistory()

  const [selectedPairId, setSelectedPairId] = useState<string | null>(null)
  const [selectedAircraftId, setSelectedAircraftId] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [startError, setStartError] = useState<string | null>(null)
  const [justCompletedFlightId, setJustCompletedFlightId] = useState<string | null>(null)
  const [historyFlight, setHistoryFlight] = useState<Flight | null>(null)

  const flightLive = useFlightLive({
    onFlightCompleted: (flightId) => {
      setJustCompletedFlightId(flightId)
      activeFlight.refetch()
      historyQuery.refetch()
    },
    onFlightNeedsResolution: () => {
      activeFlight.refetch()
    },
  })

  const routesById = useMemo(() => Object.fromEntries(routesQuery.routes.map((route) => [route.id, route])), [routesQuery.routes])

  /**
   * The route a flight was flown on, or undefined when it has none.
   *
   * A contract sector genuinely has no route - it is somebody else's aeroplane, going somewhere the
   * airline does not serve - so `routeId` is null and there is nothing to look up. That is a normal
   * state, not a missing record, and every route lookup on this page goes through here so it can
   * never be indexed with a null.
   */
  const routeFor = useCallback(
    (routeId: string | null | undefined): RouteSummary | undefined =>
      routeId ? (routesById[routeId] as RouteSummary | undefined) : undefined,
    [routesById],
  )

  // GET /flights/options' aircraftOptions doesn't carry icaoType/family (see types/flight.ts) -
  // joined in here from the buy/lease catalogue so the SimBrief hand-off and the sim-aircraft
  // readiness check still have an ICAO type to compare against. Falls back to the type's own name
  // when the catalogue hasn't loaded yet or the id is unrecognised, rather than blocking on it.
  const aircraftTypesById = useMemo(
    () => new Map(aircraftTypesQuery.types.map((type) => [type.id, type])),
    [aircraftTypesQuery.types],
  )

  const rows = useMemo<RouteRow[]>(() => {
    if (optionsQuery.status === 'ready') {
      return optionsQuery.options.map((option) => {
        const route = routesById[option.routeId] as RouteSummary | undefined
        return {
          routeId: option.routeId,
          flightNumber: option.flightNumber,
          departureIcao: option.departureIcao,
          departureName: option.departureName ?? route?.departureName ?? null,
          arrivalIcao: option.arrivalIcao,
          arrivalName: option.arrivalName ?? route?.arrivalName ?? null,
          distanceNm: option.distanceNm || route?.distanceNm || 0,
          blockMinutes: option.estimatedBlockMinutes ?? routeBlockMinutes[option.routeId] ?? null,
          baseFare: route?.baseFare ?? 0,
          isFlyable: option.isFlyable,
          reason: option.reason,
          aircraftOptions: option.aircraftOptions.map((aircraft) => {
            const type = aircraft.aircraftTypeId ? aircraftTypesById.get(aircraft.aircraftTypeId) : undefined
            return { ...aircraft, icaoType: type?.icaoType ?? aircraft.aircraftTypeName, family: type?.family ?? aircraft.aircraftTypeName }
          }),
          aircraftUnknown: false,
        }
      })
    }

    return routesQuery.routes.map((route) => ({
      routeId: route.id,
      flightNumber: null,
      departureIcao: route.departureIcao,
      departureName: route.departureName,
      arrivalIcao: route.arrivalIcao,
      arrivalName: route.arrivalName,
      distanceNm: route.distanceNm,
      blockMinutes: routeBlockMinutes[route.id] ?? null,
      baseFare: route.baseFare,
      isFlyable: true,
      reason: null,
      aircraftOptions: [],
      aircraftUnknown: true,
    }))
  }, [optionsQuery.status, optionsQuery.options, routesQuery.routes, routesById, routeBlockMinutes, aircraftTypesById])

  // Pair up outbound/return legs into one round-trip entry each (Fly screen shows one card per
  // pair, offering whichever direction is actually flyable right now) using the same
  // returnRouteId links RoutesTable pairs on, sourced from GET /routes rather than
  // GET /flights/options (which doesn't carry returnRouteId).
  const returnRouteIdByRouteId = useMemo(
    () => Object.fromEntries(routesQuery.routes.map((route) => [route.id, route.returnRouteId])),
    [routesQuery.routes],
  )
  const routePairs = useMemo(() => pairRouteRows(rows, returnRouteIdByRouteId), [rows, returnRouteIdByRouteId])

  // Default selection: prefer a "ready now" pair (aircraft already at the offered direction's
  // departure airport), then any flyable pair, then whatever's first - and re-pick if the
  // previous selection disappeared (e.g. options just loaded and it's no longer valid).
  useEffect(() => {
    if (routePairs.length === 0) {
      if (selectedPairId !== null) setSelectedPairId(null)
      return
    }
    if (selectedPairId && routePairs.some((pair) => pair.pairId === selectedPairId)) return
    const readyNow = routePairs.find((pair) => pair.isFlyable && pair.active.aircraftOptions.some((a) => a.isFlyable))
    const flyable = routePairs.find((pair) => pair.isFlyable)
    const fallback = readyNow ?? flyable ?? routePairs[0]
    if (fallback) setSelectedPairId(fallback.pairId)
  }, [routePairs, selectedPairId])

  const selectedPair = routePairs.find((pair) => pair.pairId === selectedPairId) ?? null
  const selectedRow = selectedPair?.active ?? null

  // The player may only ever fly a RESERVED aircraft - aircraftOptions lists
  // every aircraft at the departure airport, flyable or not, so the default pick (and the only
  // ones selectable at all) must be restricted to the flyable subset. Unflyable ones are still
  // shown in the brief, disabled, with their reason (see FlightBrief.tsx).
  useEffect(() => {
    if (!selectedRow) {
      if (selectedAircraftId !== null) setSelectedAircraftId(null)
      return
    }
    if (selectedAircraftId && selectedRow.aircraftOptions.some((a) => a.fleetAircraftId === selectedAircraftId && a.isFlyable)) return
    setSelectedAircraftId(selectedRow.aircraftOptions.find((a) => a.isFlyable)?.fleetAircraftId ?? null)
  }, [selectedRow, selectedAircraftId])

  const selectedAircraft = selectedRow?.aircraftOptions.find((a) => a.fleetAircraftId === selectedAircraftId) ?? null

  const flightPreview = useFlightPreview(
    selectedRow?.departureIcao ?? null,
    selectedRow?.arrivalIcao ?? null,
    selectedAircraft?.aircraftTypeId ?? null,
    selectedRow?.baseFare ?? null,
  )

  const flightPlanImport = useFlightPlanImport(selectedRow?.routeId ?? null, selectedAircraft?.fleetAircraftId ?? null)

  const simStatus = useSimStatus(activeFlight.status === 'none')

  const activeRoute = routeFor(activeFlight.data?.flight.routeId)
  // The job a contract leg belongs to, when the flight in the air is one. Supplied by
  // GET /flights/active, because a contract sector has no route to name itself with.
  const activeContract = activeFlight.data?.contract ?? null

  // Where the flight in the air is actually going, whichever kind it is. An airline sector gets this
  // from its route; a contract leg has no route and gets it from the contract block instead. Resolved
  // once, here, so the live map, the coordinate fetch and the header all agree.
  const activeDepartureIcao = activeRoute?.departureIcao ?? activeContract?.departureIcao ?? null
  const activeArrivalIcao = activeRoute?.arrivalIcao ?? activeContract?.arrivalIcao ?? null

  const neededIcaos = useMemo(() => {
    const set = new Set<string>()
    if (selectedRow) {
      set.add(selectedRow.departureIcao)
      set.add(selectedRow.arrivalIcao)
    }
    if (activeDepartureIcao) set.add(activeDepartureIcao)
    if (activeArrivalIcao) set.add(activeArrivalIcao)
    return Array.from(set)
  }, [selectedRow, activeDepartureIcao, activeArrivalIcao])

  const coordsByIcao = useAirportCoordinates(neededIcaos)

  const departureAirport = selectedRow ? coordsByIcao[selectedRow.departureIcao] : undefined
  const departureCoords = selectedRow && departureAirport
    ? { icao: selectedRow.departureIcao, latitude: departureAirport.latitude, longitude: departureAirport.longitude }
    : null

  const liveDepartureAirport = activeDepartureIcao ? coordsByIcao[activeDepartureIcao] : undefined
  const liveArrivalAirport = activeArrivalIcao ? coordsByIcao[activeArrivalIcao] : undefined
  const liveDeparture = activeDepartureIcao && liveDepartureAirport
    ? { icao: activeDepartureIcao, latitude: liveDepartureAirport.latitude, longitude: liveDepartureAirport.longitude }
    : null
  const liveArrival = activeArrivalIcao && liveArrivalAirport
    ? { icao: activeArrivalIcao, latitude: liveArrivalAirport.latitude, longitude: liveArrivalAirport.longitude }
    : null
  const livePath = liveDeparture && liveArrival ? sampleGreatCirclePath(liveDeparture.latitude, liveDeparture.longitude, liveArrival.latitude, liveArrival.longitude) : []

  const completedDetail = useFlightDetail(justCompletedFlightId)
  const historyDetail = useFlightDetail(historyFlight?.id ?? null)

  /**
   * The header a report card puts at the top of a finished flight.
   *
   * Two sources, because there are two kinds of sector. An airline flight names its route; a contract
   * leg has none, and takes its airports from the `contract` block instead - which is exactly why the
   * server sends one. Returning null would leave the card headed "Flight report" with no airports at
   * all, which is the blank-panel outcome rather than an honest one.
   */
  const reportRouteFor = useCallback(
    (detail: FlightDetail) => {
      const route = routeFor(detail.flight.routeId)
      if (route) {
        return {
          departureIcao: route.departureIcao,
          departureName: route.departureName,
          arrivalIcao: route.arrivalIcao,
          arrivalName: route.arrivalName,
          flightNumber: route.flightNumber,
        }
      }
      const contract = detail.contract
      if (contract?.departureIcao && contract.arrivalIcao) {
        return {
          departureIcao: contract.departureIcao,
          departureName: null,
          arrivalIcao: contract.arrivalIcao,
          arrivalName: null,
          // A contract sector carries no airline flight number - it is not the airline's flight.
          flightNumber: null,
        }
      }
      return null
    },
    [routeFor],
  )

  async function handleStartFlight() {
    if (!selectedRow) return
    setStarting(true)
    setStartError(null)
    try {
      const { planSource, planMessage } = await post<StartFlightResponse>('/flights/start', {
        routeId: selectedRow.routeId,
        ...(selectedAircraft ? { fleetAircraftId: selectedAircraft.fleetAircraftId } : {}),
      })
      toast.success(`Flight ${selectedRow.departureIcao} → ${selectedRow.arrivalIcao} started.`)
      // Say so plainly: the pre-flight banner already covered
      // this for the common case, but a fallback that happened right at the moment of starting
      // (e.g. SimBrief timed out) still deserves its own word, not just a silent substitution.
      if (planSource !== 'SimBrief' && planMessage) {
        toast.info(planMessage)
      }
      activeFlight.refetch()
    } catch (err) {
      setStartError(describeError(err, 'Could not start this flight. Check your connection and try again.'))
    } finally {
      setStarting(false)
    }
  }

  async function handleAbandon(flightId: string) {
    try {
      await post(`/flights/${flightId}/abandon`)
      toast.success('Flight abandoned.')
      activeFlight.refetch()
      historyQuery.refetch()
    } catch (err) {
      toast.error(describeError(err, 'Could not abandon this flight.'))
      throw err
    }
  }

  async function handleCompleteWithEstimates(flightId: string) {
    try {
      await post(`/flights/${flightId}/complete-manual`)
      toast.success('Flight completed with estimates.')
      setJustCompletedFlightId(flightId)
      activeFlight.refetch()
      historyQuery.refetch()
    } catch (err) {
      toast.error(describeError(err, 'Could not complete this flight.'))
      throw err
    }
  }

  function handleFlyAgain() {
    setJustCompletedFlightId(null)
    setSelectedPairId(null)
    setSelectedAircraftId(null)
    optionsQuery.refetch()
    routesQuery.refetch()
  }

  const airlineIcaoCode = airlineSummary.data?.airline.icaoCode ?? null

  // 1. Just-finished flight - the hero report card, shown once regardless of what GET
  //    /flights/active now says (it will have already flipped to "none").
  if (justCompletedFlightId) {
    return (
      <div>
        <PageHeader
          title="Flight complete"
          description="Here's how it went."
          actions={
            <Button type="button" variant="outline" onClick={handleFlyAgain} className="gap-2">
              <RotateCcw className="size-4" />
              Fly again
            </Button>
          }
        />
        {completedDetail.status === 'loading' && (
          <div className="space-y-4">
            <Skeleton className="h-64 w-full" />
            <Skeleton className="h-40 w-full" />
          </div>
        )}
        {completedDetail.status === 'error' && <p className="text-sm text-danger">{completedDetail.errorMessage}</p>}
        {completedDetail.status === 'ready' && completedDetail.data && (
          <ReportCard
            detail={completedDetail.data}
            route={reportRouteFor(completedDetail.data)}
            airlineIcaoCode={airlineIcaoCode}
            track={
              <FlightTrackCard
                flightId={completedDetail.data.flight.id}
                departureIcao={reportRouteFor(completedDetail.data)?.departureIcao ?? null}
                arrivalIcao={reportRouteFor(completedDetail.data)?.arrivalIcao ?? null}
              />
            }
          />
        )}
      </div>
    )
  }

  // 2. Still checking whether a flight is already in progress (page load / refresh).
  if (activeFlight.status === 'loading') {
    return (
      <div>
        <PageHeader title="Fly" description="Track an active flight in MSFS in real time." />
        <Skeleton className="h-96 w-full" />
      </div>
    )
  }

  if (activeFlight.status === 'error') {
    return (
      <div>
        <PageHeader title="Fly" description="Track an active flight in MSFS in real time." />
        <EmptyState
          icon={PlaneTakeoff}
          title="Could not reach the server"
          description={activeFlight.errorMessage ?? 'Check your connection and try again.'}
          action={
            <Button type="button" variant="outline" onClick={activeFlight.refetch}>
              Try again
            </Button>
          }
        />
      </div>
    )
  }

  // 3. A flight is in progress.
  if (activeFlight.status === 'active' && activeFlight.data) {
    const { flight, needsResolution } = activeFlight.data
    // Names the sector whichever kind it is. Only genuinely unknown when neither a route nor a
    // contract could be resolved, which is a broken record rather than a normal state.
    const routeLabel =
      activeDepartureIcao && activeArrivalIcao
        ? `${activeDepartureIcao} → ${activeArrivalIcao}`
        : 'Flight in progress'

    return (
      <div>
        <PageHeader
          title="Fly"
          description={
            activeContract
              ? `Contract leg ${activeContract.legSequence} of ${activeContract.legCount} for ${activeContract.operatorName}.`
              : 'Track an active flight in MSFS in real time.'
          }
        />
        {needsResolution ? (
          <NeedsResolutionPanel
            onResume={activeFlight.refetch}
            onCompleteWithEstimates={() => handleCompleteWithEstimates(flight.id)}
            onAbandon={() => handleAbandon(flight.id)}
          />
        ) : (
          <LiveFlightView
            flight={flight}
            live={flightLive.flightUpdate?.flightId === flight.id ? flightLive.flightUpdate : activeFlight.data.live}
            telemetry={flightLive.telemetry}
            hubConnected={flightLive.hubStatus === 'connected'}
            departure={liveDeparture}
            arrival={liveArrival}
            path={livePath}
            routeLabel={routeLabel}
            flightNumber={activeRoute?.flightNumber ?? null}
            airlineIcaoCode={airlineIcaoCode}
            onAbandon={() => handleAbandon(flight.id)}
          />
        )}
      </div>
    )
  }

  // 4. Pre-flight: pick a route, review the brief, start flying.
  return (
    <div className="space-y-6">
      <PageHeader title="Fly" description="Pick a route, review the brief, and start flying." />

      {routePairs.length === 0 && routesQuery.status === 'ready' ? (
        <EmptyState
          icon={PlaneTakeoff}
          title="No routes to fly yet"
          description="Build a route on the Routes page, then come back here to fly it."
        />
      ) : (
        <div className="grid gap-4 lg:grid-cols-[minmax(320px,420px)_minmax(320px,1fr)] lg:items-start">
          <RouteSelector
            pairs={routePairs}
            selectedPairId={selectedPairId}
            onSelect={(pair) => setSelectedPairId(pair.pairId)}
            optionsUnavailable={optionsQuery.status === 'unavailable' || optionsQuery.status === 'error'}
            airlineIcaoCode={airlineIcaoCode}
          />

          {selectedRow ? (
            <div className="space-y-3">
              <FlightPlanImportBanner
                planImport={flightPlanImport.data}
                status={flightPlanImport.status}
                onRefresh={flightPlanImport.refresh}
              />
              <FlightBrief
                row={selectedRow}
                aircraftOptions={selectedRow.aircraftOptions}
                selectedAircraft={selectedAircraft}
                onSelectAircraft={(aircraft) => setSelectedAircraftId(aircraft.fleetAircraftId)}
                preview={flightPreview.data}
                previewStatus={flightPreview.status}
                airlineIcaoCode={airlineIcaoCode}
                simStatus={simStatus.status}
                simStatusLoaded={simStatus.loaded}
                telemetry={flightLive.telemetry}
                departureCoords={departureCoords}
                onStart={handleStartFlight}
                starting={starting}
                startError={startError}
              />
            </div>
          ) : (
            <EmptyState icon={PlaneTakeoff} title="Pick a route" description="Choose a route on the left to see its flight brief." />
          )}
        </div>
      )}

      <FlightHistoryList
        flights={historyQuery.flights}
        status={historyQuery.status}
        routesById={routesById}
        airlineIcaoCode={airlineIcaoCode}
        onView={setHistoryFlight}
      />

      <Dialog open={historyFlight !== null} onOpenChange={(open) => !open && setHistoryFlight(null)}>
        <DialogContent className="max-h-[85vh] max-w-2xl overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Flight report</DialogTitle>
          </DialogHeader>
          {historyDetail.status === 'loading' && <Skeleton className="h-64 w-full" />}
          {historyDetail.status === 'error' && <p className="text-sm text-danger">{historyDetail.errorMessage}</p>}
          {historyDetail.status === 'ready' && historyDetail.data && historyFlight && (
            <ReportCard
              detail={historyDetail.data}
              route={reportRouteFor(historyDetail.data)}
              airlineIcaoCode={airlineIcaoCode}
              track={
                <FlightTrackCard
                  flightId={historyFlight.id}
                  departureIcao={reportRouteFor(historyDetail.data)?.departureIcao ?? null}
                  arrivalIcao={reportRouteFor(historyDetail.data)?.arrivalIcao ?? null}
                />
              }
            />
          )}
        </DialogContent>
      </Dialog>
    </div>
  )
}
