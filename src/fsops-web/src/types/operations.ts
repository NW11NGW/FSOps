/**
 * GET /api/v1/operations/live - backs the dashboard's live operations map (see docs/PLAN.md
 * "Live operations map"). `player` aircraft are the pilot's own tracked flight, driven by real
 * telemetry; `virtual` aircraft are other pilots' scheduled occurrences, their position
 * interpolated server-side from the route's great circle and elapsed block time - nothing here
 * is stored, so it stays consistent with the deterministic wall-clock catch-up model.
 *
 * `aircraftType` and `headingDeg` are typed as nullable/optional even though the backend
 * populates both for real flights now (OperationsEndpoints.cs joins AircraftTypes for both kinds,
 * and `player` heading comes from LiveFlightSnapshot.TrueHeadingDeg): `aircraftType` can still be
 * null if a flight's FleetAircraft record didn't resolve, so the UI must keep degrading
 * gracefully rather than assuming presence.
 */
export type LiveAircraftKind = 'player' | 'virtual'

export interface LiveAircraft {
  kind: LiveAircraftKind
  flightId: string | null
  pilotName: string
  registration: string | null
  aircraftType?: string | null
  routeId: string | null
  flightNumber: string | null
  departureIcao: string | null
  arrivalIcao: string | null
  latitudeDeg: number
  longitudeDeg: number
  headingDeg: number | null
  phase: string
  percentComplete: number | null
  departureUtc: string
  estimatedArrivalUtc: string
  elapsedMinutes: number
  remainingMinutes: number
}

export interface LiveNetworkRoute {
  routeId: string
  departureIcao: string
  departureLat: number
  departureLon: number
  arrivalIcao: string
  arrivalLat: number
  arrivalLon: number
  distanceNm: number
}

export interface LiveOperationsResponse {
  aircraft: LiveAircraft[]
  network: LiveNetworkRoute[]
}

/**
 * GET /api/v1/operations/atc - backs the dashboard's ATC layer (see docs/PLAN.md "VATSIM
 * integration"). Deliberately narrower than the full network-traffic integration described
 * there: this is online controllers only, and only ones covering an airport in the airline's own
 * route network - never a global controller list, and never other pilots' traffic.
 *
 * `status` distinguishes "the feed answered but nobody relevant is online" (`'ok'` with an empty
 * list) from "the feed itself could not be read" (`'unavailable'`) - the UI must tell these apart
 * rather than showing the same empty state for both.
 */
export type VatsimAtcStatus = 'ok' | 'unavailable'

export interface VatsimAtcController {
  callsign: string
  facilityLabel: string
  frequency: string
  /** Null when the callsign doesn't resolve to one of the airline's network airports (en-route/
   *  oceanic CTR/FSS positions, or an airport-shaped callsign FSOps doesn't recognise) - such
   *  controllers are dropped server-side before this ever reaches the client, so in practice
   *  every entry here has a resolved airport. Left nullable to match the wire contract exactly
   *  rather than assuming that invariant holds forever. */
  airportIcao: string | null
  airportName: string | null
  latitudeDeg: number | null
  longitudeDeg: number | null
  visualRangeNm: number | null
  logonTimeUtc: string
}

export interface VatsimAtcResponse {
  status: VatsimAtcStatus
  fetchedAtUtc: string | null
  controllers: VatsimAtcController[]
}
