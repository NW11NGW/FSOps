export type FlightPhase =
  | 'Preflight'
  | 'TaxiOut'
  | 'TakeoffRoll'
  | 'Climb'
  | 'Cruise'
  | 'Descent'
  | 'Approach'
  | 'Landed'
  | 'TaxiIn'
  | 'Shutdown'

export type FlightStatus = 'Planned' | 'InProgress' | 'Completed' | 'Interrupted' | 'Abandoned'

export type FlightEventType = 'PhaseChange' | 'Touchdown' | 'PositionSnapshot' | 'Mismatch' | 'Note'

/** GET/POST /flights/* flight DTO shape (FlightEndpoints.ToFlightDto). */
export interface Flight {
  id: string
  airlineId: string
  routeId: string
  fleetAircraftId: string
  pilotId: string
  status: FlightStatus
  plannedDepartureUtc: string
  plannedBlockMinutes: number
  outUtc: string | null
  offUtc: string | null
  onUtc: string | null
  inUtc: string | null
  paxBooked: number
  paxFlown: number
  fuelPlannedKg: number
  fuelUsedKg: number
  landingFpmFirst: number | null
  landingFpmHardest: number | null
  landingGForce: number | null
  centrelineDeviationM: number | null
  titleFlown: string
  typeMismatch: boolean
  revenue: number
  totalCost: number
  createdUtc: string
}

export interface FlightEvent {
  id: string
  utc: string
  type: FlightEventType
  payloadJson: string
}

/** GET /flights/{id} response. */
export interface FlightDetail {
  flight: Flight
  events: FlightEvent[]
}

/** Mismatch FlightEvent payload (see FlightEndpoints.StartAsync). */
export interface MismatchPayload {
  titleFlown: string
  atcModel: string | null
  expectedFamily: string
  expectedType: string
}

/** LiveFlightSnapshot record - GET /flights/active `live` field and the `flightUpdate` hub event. */
export interface LiveFlightSnapshot {
  flightId: string
  phase: FlightPhase
  latitudeDeg: number
  longitudeDeg: number
  altitudeMslFt: number
  altitudeAglFt: number
  indicatedAirspeedKt: number
  groundSpeedKt: number
  verticalSpeedFpm: number
  fuelRemainingKg: number
  elapsedBlockMinutes: number
  plannedBlockMinutes: number
  awaitingSimReconnect: boolean
  timestampUtc: string
}

/** GET /flights/active response (200 body; 204 means no active flight). */
export interface ActiveFlightResponse {
  flight: Flight
  needsResolution: boolean
  live: LiveFlightSnapshot | null
}

/** SimTelemetryService.BroadcastAsync payload - the `telemetry` hub event, ~2 Hz. */
export interface TelemetryPayload {
  timestampUtc: string
  latitude: number
  longitude: number
  altitudeMslFt: number
  altitudeAglFt: number
  indicatedAirspeedKt: number
  groundSpeedKt: number
  verticalSpeedFpm: number
  headingTrue: number
  headingMagnetic: number
  onGround: boolean
  connectionState: 'Disconnected' | 'Connecting' | 'Connected'
}

/** GET /sim/status (SimStatusResponse record). */
export interface SimStatus {
  state: 'Disconnected' | 'Connecting' | 'Connected'
  sourceKind: string
  aircraftTitle: string | null
  lastSampleUtc: string | null
}

/** One fleet aircraft offered for a flyable route by GET /flights/options. */
export interface FlightOptionAircraft {
  fleetAircraftId: string
  registration: string
  aircraftTypeId: string
  aircraftTypeName: string
  icaoType: string
  family: string
  locationIcao: string
  paxCapacity: number | null
}

/** One row of GET /flights/options - a route plus whether it can be flown right now. */
export interface FlightOption {
  routeId: string
  flightNumber: string | null
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  distanceNm: number
  blockMinutes: number | null
  isFlyable: boolean
  reason: string | null
  availableAircraft: FlightOptionAircraft[]
}

export interface StartFlightRequest {
  routeId: string
  fleetAircraftId?: string
}
