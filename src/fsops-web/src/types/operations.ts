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
