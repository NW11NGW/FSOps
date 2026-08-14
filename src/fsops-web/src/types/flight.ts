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

/**
 * Every status a flight row can actually carry, matching FlightStatus in Enums.cs. The last three
 * belong to virtual pilots' scheduled occurrences: Skipped and Cancelled are the Casual and
 * True-life outcomes for a sector that could not fly, and Suspended is one paused by an aircraft's
 * maintenance check. They are returned by GET /flights alongside everything else, so any component
 * that maps over this union has to handle them.
 *
 * There is no 'Planned' - nothing has ever written it, and the member was removed from the backend
 * enum on 2026-08-14.
 */
export type FlightStatus =
  | 'InProgress'
  | 'Completed'
  | 'Interrupted'
  | 'Abandoned'
  | 'Skipped'
  | 'Cancelled'
  | 'Suspended'

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
  /** G8: whether FSOps corroborated this flight as genuinely flown online on VATSIM - callsign,
   *  position and timing checked against FSOps' own telemetry, never merely "the CID was online
   *  somewhere". Three-valued, same discipline as `typeMismatch`: null means never checked (no CID
   *  configured, feature off, or the feed was unavailable the whole flight) - render that as
   *  "nothing to show", never as "not online", which would be a false negative for every flight
   *  flown before this feature existed too. */
  vatsimOnline: boolean | null
  /** The callsign the configured CID was flying under when last corroborated. Null unless
   *  vatsimOnline is true. */
  vatsimCallsign: string | null
  /** Fraction (0-1) of this flight's corroboration checks that matched - "how much of the flight
   *  was online". Null when vatsimOnline is null (never checked). */
  vatsimOnlineFraction: number | null
  /** Comma-separated ATC callsigns worked at departure/arrival while this flight was corroborated
   *  online. Null/empty if none. */
  vatsimControllersWorked: string | null
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
  | 'StartingCapital'
  | 'LoanProceeds'
  | 'CancellationFee'
  | 'VatsimOnlineBonus'
  // Never actually posted against a flight (a repositioning line is airline-level, with no
  // FlightId), but kept in step with the backend enum and lib/ledgerCategory.ts's own copy - these
  // are meant to be the same list, and a union that quietly diverges is how a genuinely-missing
  // category ends up rendering as a raw string one day.
  | 'AircraftRepositioning'
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
   *  is gone - fuel is a persisted asset on the airframe, not a per-flight figure. */
  aircraftFuelOnBoardKg: number | null
}

/**
 * One row of GET /flights/logbook - a sector that was actually attempted.
 *
 * Only sectors that happened are here: Completed, Interrupted and Abandoned. A Planned row has not
 * flown yet, and the Skipped/Cancelled/Suspended virtual-pilot statuses never left the gate.
 *
 * `revenue`/`cost`/`net` are summed from the flight's own posted ledger rows - the same
 * append-only source the cash balance sums, never a cached column. `net` is deliberately the
 * identical figure the flight's own report card shows as "Net", so clicking a row never reveals a
 * different number.
 */
export interface LogbookSector {
  flightId: string
  status: FlightStatus
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  registration: string | null
  aircraftTypeName: string | null
  aircraftIcaoType: string | null
  pilotName: string | null
  isPlayerFlight: boolean
  /** What the logbook sorts and groups by: when the sector finished if it did, else when it left,
   *  else when it was planned for. Never a fabricated stand-in. */
  dateUtc: string
  outUtc: string | null
  inUtc: string | null
  plannedBlockMinutes: number
  /** Measured Out-to-In minutes. Null when either stamp is missing, and also null when the sim ran
   *  faster than real time - elapsed wall time means nothing then, so "not measured" is the only
   *  honest answer. */
  actualBlockMinutes: number | null
  /** True when block time is unmeasurable because of time acceleration, so the UI can say why
   *  rather than just showing a dash. */
  blockTimeNotMeasured: boolean
  paxFlown: number
  paxBooked: number
  /** Seats on the aircraft this sector was flown by. Null when the type can no longer be resolved. */
  seats: number | null
  loadFactorPercent: number | null
  landingFpmFirst: number | null
  fuelUsedKg: number
  revenue: number
  cost: number
  net: number
  simRateElevated: boolean
  slewDetected: boolean
  positionJumpDetected: boolean
  vatsimOnline: boolean | null
  /** Whether this sector has a recorded flown track to draw. False for every flight that predates
   *  position snapshots and for every virtual-pilot sector - those never had a simulator attached
   *  and write no events at all. */
  hasTrack: boolean
  /** How many position samples were recorded. 0 whenever `hasTrack` is false. */
  trackPointCount: number
}

/** GET /flights/logbook response. `totalSectors` is the real total; `returnedSectors` is how many
 *  of them this response carries (the newest ones), so the UI can say it is showing a slice. */
export interface LogbookResponse {
  totalSectors: number
  returnedSectors: number
  sectors: LogbookSector[]
}

/** One recorded position sample of a flown track - see GET /flights/{id}/track. */
export interface FlightTrackPoint {
  utc: string
  lat: number
  /** Degrees east exactly as recorded, never normalised - a path crossing the antimeridian is the
   *  renderer's problem to split (see lib/geo splitAntimeridian), not the record's to rewrite. */
  lon: number
  altMslFt: number | null
  gsKt: number | null
  phase: FlightPhase | null
}

/**
 * GET /flights/{id}/track - the path a flight actually flew, from its ~15-second position
 * snapshots. An empty `points` array is a legitimate answer, not an error: older flights predate
 * position snapshots, and every virtual-pilot flight had no simulator attached and recorded none.
 */
export interface FlightTrack {
  flightId: string
  /** Samples actually recorded, before any thinning - always the honest total. */
  recordedPointCount: number
  /** True when `points` is an evenly-spaced subsample taken purely to keep the payload and the
   *  render cheap. The first and last points are always kept, so the track still begins and ends
   *  where it really did, and the stored rows are untouched. */
  thinned: boolean
  points: FlightTrackPoint[]
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
 * (the 2026-08-09 defect this fixes was silently dropping the ones that weren't, which taught the
 * player nothing) - `isFlyable`/`reason` say which. This does NOT carry `icaoType`/`family`; join against
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
