import { useEffect, useRef, useState, type ReactNode } from 'react'

import { HubStatusPill } from '@/components/layout/ConnectionPill'
import { PanelDebrief } from '@/components/panel/PanelDebrief'
import { PanelIdle } from '@/components/panel/PanelIdle'
import { PanelInFlight } from '@/components/panel/PanelInFlight'
import { PanelReconnecting } from '@/components/panel/PanelReconnecting'
import { PanelSkeleton } from '@/components/panel/PanelSkeleton'
import { useActiveFlight } from '@/hooks/useActiveFlight'
import { useAirlineSummary } from '@/hooks/useAirlineSummary'
import { useFleet } from '@/hooks/useFleet'
import { useFlightHistory } from '@/hooks/useFlightHistory'
import { useFlightLive } from '@/hooks/useFlightLive'
import { useFlightOptions } from '@/hooks/useFlightOptions'
import { useRoutes } from '@/hooks/useRoutes'
import { isRecentlyLanded, selectMaintenanceWarning, selectNextFlights } from '@/lib/panelFormat'

// Matches the retry cadence useLiveConnection/useFlightLive already use for their own hub
// reconnect attempts, so a REST call that failed during a server restart gets picked back up on
// the same rhythm rather than sitting on a stale error forever.
const RETRY_DELAY_MS = 5000

// How long after landing a flight still reads as "just landed" for a panel that was reopened
// (rather than left open through the whole sector) - see isRecentlyLanded.
const RECENT_LANDING_WINDOW_MINUTES = 15

/**
 * The compact /panel route MSFS's toolbar loads in an iframe. Read-only and glanceable: in the
 * air it's flight number/callsign, departure/arrival, phase, progress, ETA, block time remaining,
 * fuel, passengers, cash, and what's next; on the ground after landing it's the debrief - landing
 * rate, block variance, what the sector earned, plus any maintenance warning worth a glance.
 *
 * Reuses the exact same /hubs/live SignalR connection and REST endpoints the Fly screen already
 * consumes (useFlightLive, useActiveFlight, useFlightHistory) rather than a second telemetry
 * path, and never imports anything that pulls in maplibre-gl - there is no map here, only numbers.
 */
export function Panel() {
  const activeFlight = useActiveFlight()
  const airlineSummary = useAirlineSummary()
  const routesQuery = useRoutes()
  const optionsQuery = useFlightOptions()
  const historyQuery = useFlightHistory()
  const fleetQuery = useFleet()

  const [justCompletedFlightId, setJustCompletedFlightId] = useState<string | null>(null)

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

  // The panel must survive the server restarting mid-flight rather than becoming a broken white
  // box. useActiveFlight only fetches on mount/refetch - it has no retry loop of its own - so if
  // the first (or a later) call lands while the server is down, keep trying on the same cadence
  // the hub connection already retries at.
  useEffect(() => {
    if (activeFlight.status !== 'error') return
    const timer = setTimeout(() => activeFlight.refetch(), RETRY_DELAY_MS)
    return () => clearTimeout(timer)
  }, [activeFlight.status, activeFlight.refetch])

  // SignalR reconnecting doesn't itself refresh the REST snapshot it missed while it was away -
  // pull a fresh one the moment the hub comes back so a restart doesn't leave stale data on
  // screen indefinitely once the connection recovers.
  const wasConnected = useRef(flightLive.hubStatus === 'connected')
  useEffect(() => {
    if (flightLive.hubStatus === 'connected' && !wasConnected.current) {
      activeFlight.refetch()
      historyQuery.refetch()
    }
    wasConnected.current = flightLive.hubStatus === 'connected'
  }, [flightLive.hubStatus, activeFlight.refetch, historyQuery.refetch])

  // Once a new flight starts, the previous one's debrief has done its job - clear it so the panel
  // switches back to the in-air view instead of showing a stale report forever.
  useEffect(() => {
    if (justCompletedFlightId && activeFlight.data && activeFlight.data.flight.id !== justCompletedFlightId) {
      setJustCompletedFlightId(null)
    }
  }, [justCompletedFlightId, activeFlight.data])

  const routesById = new Map(routesQuery.routes.map((route) => [route.id, route]))
  const airlineIcaoCode = airlineSummary.data?.airline.icaoCode ?? null
  const maintenanceWarning = selectMaintenanceWarning(fleetQuery.fleet)
  const nextFlights = selectNextFlights(optionsQuery.options)

  const latestHistoryFlight = historyQuery.flights[0] ?? null
  const debriefFlight = justCompletedFlightId
    ? (historyQuery.flights.find((f) => f.id === justCompletedFlightId) ?? null)
    : !activeFlight.data && latestHistoryFlight && isRecentlyLanded(latestHistoryFlight, new Date().toISOString(), RECENT_LANDING_WINDOW_MINUTES)
      ? latestHistoryFlight
      : null

  let content: ReactNode
  if (debriefFlight) {
    content = (
      <PanelDebrief
        flight={debriefFlight}
        route={routesById.get(debriefFlight.routeId) ?? null}
        airlineIcaoCode={airlineIcaoCode}
        maintenanceWarning={maintenanceWarning}
      />
    )
  } else if (activeFlight.data) {
    const { flight } = activeFlight.data
    const live = flightLive.flightUpdate?.flightId === flight.id ? flightLive.flightUpdate : activeFlight.data.live
    content = (
      <PanelInFlight
        flight={flight}
        live={live}
        route={routesById.get(flight.routeId) ?? null}
        airlineIcaoCode={airlineIcaoCode}
        cashBalance={airlineSummary.data?.cashBalance ?? null}
        hubConnected={flightLive.hubStatus === 'connected'}
        nextFlights={nextFlights}
        maintenanceWarning={maintenanceWarning}
      />
    )
  } else if (activeFlight.status === 'loading') {
    content = <PanelSkeleton />
  } else if (activeFlight.status === 'error') {
    content = <PanelReconnecting />
  } else {
    content = (
      <PanelIdle
        cashBalance={airlineSummary.data?.cashBalance ?? null}
        nextFlights={nextFlights}
        airlineIcaoCode={airlineIcaoCode}
        maintenanceWarning={maintenanceWarning}
      />
    )
  }

  return (
    <div className="min-h-screen bg-background px-3 py-3 text-foreground">
      <div className="mb-3 flex items-center justify-between gap-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">FSOps</span>
        <HubStatusPill status={flightLive.hubStatus} />
      </div>
      {content}
    </div>
  )
}
