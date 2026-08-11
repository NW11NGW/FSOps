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
  /** Informational only - never affects payment. True is a genuine family mismatch, false is a
   *  confirmed match, null means the sim reported no aircraft to check (not connected, or no
   *  aircraft loaded yet) - render null the same as "nothing to show", never as a mismatch. */
  typeMismatch: boolean | null
  /** True if the sim ran faster than real time at any point during this flight - see FlightIntegrityMonitor. Block-time variance and on-time performance are not measured when true; landing quality is unaffected. */
  simRateElevated: boolean
  /** Highest simulation rate observed. 1.0 (normal speed) if simRateElevated is false. */
  maxSimulationRateObserved: number
  /** True if slew mode was active at any point - invalidates the sector for payment. */
  slewDetected: boolean
  /** True if telemetry implied a physically impossible position jump - invalidates the sector for payment. */
  positionJumpDetected: boolean
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

export type LedgerCategory =
  | 'TicketRevenue'
  | 'Fuel'
  | 'LandingFees'
  | 'Handling'
  | 'Maintenance'
  | 'Salary'
  | 'CrewCost'
  | 'ParkingFees'
  | 'PassengerCharges'
  | 'TurnaroundFees'
  | 'LeasePayment'
  | 'LoanPayment'
  | 'AircraftPurchase'
  | 'Insurance'
  | 'GsxServices'
  | 'StartingCapital'
  | 'LoanProceeds'
  | 'Other'

/** One posted LedgerTransaction for a flight - the itemised financial outcome the report card
 *  shows, straight from the append-only ledger rather than a recomputation. */
export interface FlightLedgerLine {
  id: string
  utc: string
  category: LedgerCategory
  /** Signed - positive is money in, negative is money out. */
  amount: number
  description: string
}

/** GET /flights/{id} response. */
export interface FlightDetail {
  flight: Flight
  events: FlightEvent[]
  ledgerTransactions: FlightLedgerLine[]
  /** The fleet aircraft's CURRENT persisted fuel (FleetAircraft.FuelOnBoardKg) - reads as "fuel
   *  remaining after this flight" for the common case of viewing the report card right after
   *  landing, but drifts once a later flight has flown this aircraft. Null if the aircraft record
   *  is gone. See docs/PLAN.md "Persistent fuel state and tankering". */
  aircraftFuelOnBoardKg: number | null
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

/**
 * One fleet aircraft PHYSICALLY AT a route's departure airport, as returned by
 * GET /flights/options' `aircraftOptions`. Every aircraft present is listed here - flyable or not
 * (docs/PLAN.md "2b"; the 2026-08-09 defect this fixes was silently dropping the ones that
 * weren't) - `isFlyable`/`reason` say which. This does NOT carry `icaoType`/`family`; join against
 * GET /fleet/aircraft-types by `aircraftTypeId` for those (see routeRow.ts's `AircraftOptionRow`),
 * needed for the SimBrief hand-off and the sim-aircraft readiness check.
 */
export interface FlightOptionAircraft {
  fleetAircraftId: string
  registration: string
  aircraftTypeId: string | null
  aircraftTypeName: string
  paxCapacity: number | null
  estimatedBlockMinutes: number | null
  isFlyable: boolean
  reason: string | null
}

/** One row of GET /flights/options - a route plus whether it can be flown right now.
 *  `isFlyable` is true iff at least one entry in `aircraftOptions` is flyable; `reason` is only
 *  ever set when `aircraftOptions` is empty (no fleet aircraft at all at the departure airport) -
 *  when aircraft ARE present but none is flyable, their own per-aircraft reasons already cover it. */
export interface FlightOption {
  routeId: string
  flightNumber: string | null
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  distanceNm: number
  estimatedBlockMinutes: number | null
  isFlyable: boolean
  reason: string | null
  aircraftOptions: FlightOptionAircraft[]
}

export interface StartFlightRequest {
  routeId: string
  fleetAircraftId?: string
}

/** POST /flights/start response (FlightEndpoints.ToFlightStartDto) - the flight itself plus which
 *  provider's plan was actually used for its planned block time/fuel, and why on a fallback. */
export interface StartFlightResponse {
  flight: Flight
  planSource: string
  planMessage: string | null
}

/**
 * GET /flights/plan-import response (FlightEndpoints.PlanImportAsync) - the SimBrief import
 * hand-off. Read-only preview of the same plan StartAsync would apply for this route/aircraft
 * pair: `fromSimBrief` true means a real OFP matched and was used; false means the built-in
 * estimate was used instead, with `message` explaining why (no Pilot ID, unknown Pilot ID, a
 * network problem, or an OFP for a different city pair) when relevant.
 */
export interface FlightPlanImport {
  available: boolean
  source: string | null
  fromSimBrief: boolean
  message: string | null
  blockFuelKg: number | null
  cruiseAltitudeFt: number | null
  blockTimeMinutes: number | null
  routeString: string | null
}
