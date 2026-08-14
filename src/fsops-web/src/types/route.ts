export interface BlockTimeBreakdown {
  taxiOutMinutes: number
  climbMinutes: number
  cruiseMinutes: number
  descentMinutes: number
  taxiInMinutes: number
  totalMinutes: number
}

export interface FuelBreakdown {
  tripFuelKg: number
  taxiFuelKg: number
  contingencyFuelKg: number
  alternateFuelKg: number
  finalReserveFuelKg: number
  totalFuelKg: number
}

/**
 * What the airline as a whole can do about this sector length - never a statement about the single
 * aircraft type the preview figures happen to be planned with. Only `blocking` may ever stop a
 * route being created: "nothing you have reserved can fly it, but that 787 can" is guidance, and
 * treating it as a refusal made an otherwise perfectly good route a dead end.
 */
export interface RoutePreviewRange {
  verdict: 'NotAssessed' | 'ReservedCanFly' | 'RequiresReservation' | 'BeyondFleet'
  /** True only when NOTHING in the fleet can fly this far. */
  blocking: boolean
  /** The sentence to show, already written by the backend. Null when there is nothing to say. */
  message: string | null
  /** The aircraft the message names - to reserve, or the longest-legged one you have. */
  aircraftRegistration: string | null
  aircraftTypeName: string | null
  operationalRangeNm: number | null
}

/**
 * The runway mirror of RoutePreviewRange - whether the airline as a whole can physically use both
 * airports' runways (length AND surface), never a statement about the single aircraft type the
 * preview figures happen to be planned with. Only `blocking` may ever stop a route being created.
 */
export interface RoutePreviewRunway {
  verdict: 'NotAssessed' | 'ReservedCanUse' | 'RequiresReservation' | 'BeyondFleet'
  /** True only when NOTHING in the fleet can physically use both runways. */
  blocking: boolean
  /** The sentence to show, already written by the backend. Null when there is nothing to say. */
  message: string | null
  /** The aircraft the message names - to reserve, or the most runway-tolerant one you have. */
  aircraftRegistration: string | null
  aircraftTypeName: string | null
}

export interface RoutePreviewValidation {
  /**
   * Whether the aircraft type these figures were planned with can reach that far - a statement
   * about ONE type, kept for display. Never block on it: `range` is the airline-wide verdict.
   */
  withinRange: boolean
  sameAirport: boolean
  warnings: string[]
  range: RoutePreviewRange
  /** The airline-wide verdict for runway length AND surface - see RoutePreviewRunway. */
  runway: RoutePreviewRunway
}

/** Longitude/latitude pair, matching the backend's [lon, lat] point order (not [lat, lon]). */
export type LonLat = [number, number]

/**
 * The live "at this fare, expect ~N passengers (X% load factor), ~£Y revenue per sector"
 * readout - the real DemandCalculator/FareDemandModel engine, not the rough distance-only
 * suggestedFare estimate above it. Null when there's no airline yet or the pick doesn't resolve
 * to a real sector (same airport, no distance) - see RouteEndpoints.PreviewAsync.
 */
export interface RouteEconomicsPreview {
  fare: number
  referenceFare: number
  expectedPassengers: number
  loadFactorPercent: number
  expectedRevenuePerSector: number
  /** Every non-ticket line the ledger will post for this sector, fuel included at the departure
   *  airport's price. A fare decision the player can only see one side of is not a decision. */
  expectedCostPerSector: number
  expectedProfitPerSector: number
}

export interface RoutePreviewResponse {
  distanceNm: number
  initialBearingDeg: number
  estimatedBlockMinutes: number
  blockTimeBreakdown: BlockTimeBreakdown | null
  cruiseAltitudeFt: number
  blockFuelKg: number
  fuelBreakdown: FuelBreakdown | null
  /** Current price per kg at the departure airport - what this sector will actually be billed per
   *  kg of fuel burned, since a sector is charged at its own departure airport's price. */
  fuelPricePerKg: number | null
  suggestedFare: number
  economics: RouteEconomicsPreview | null
  greatCirclePath: LonLat[]
  validation: RoutePreviewValidation
}

export interface RoutePreviewRequest {
  departureIcao: string
  arrivalIcao: string
  aircraftTypeId?: string
  /** Base-currency fare to price the live economics readout against. Omitted (or non-positive)
   *  falls back to the suggested fare server-side - no validation beyond that, on purpose:
   *  the simulation is the guardrail, not an input cap. */
  fare?: number
}

/**
 * POST /routes contract. Every route is a there-and-back pair: this always creates the
 * requested direction *and* its reverse leg in one call, sharing baseFare (when supplied)
 * between both legs. flightNumber, if supplied, applies to the outbound leg only - the return
 * leg's flight number is always auto-suggested to follow on from it.
 */
export interface CreateRouteRequest {
  departureIcao: string
  arrivalIcao: string
  aircraftTypeId?: string
  baseFare?: number
  flightNumber?: string
}

/** One directional leg, as returned by GET /routes, POST /routes, PUT /routes/{id}. */
export interface RouteSummary {
  id: string
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  flightNumber: string | null
  /** The id of this route's reverse leg, if one exists - routes are meant to always have one. */
  returnRouteId: string | null
  distanceNm: number
  baseFare: number
  /** Only populated by GET /routes, which resolves the airline's fleet aircraft to estimate it. */
  estimatedBlockMinutes?: number | null
  isActive: boolean
  createdUtc: string
}

/** POST /routes response: both legs of the pair it just created (or completed, for a legacy leg). */
export interface CreateRoutePairResponse {
  outbound: RouteSummary
  inbound: RouteSummary
}

/** DELETE /routes/{id} response: both legs it removed (or just the one, for an unpaired legacy leg). */
export interface DeleteRoutePairResponse {
  deletedRouteIds: string[]
  message: string
}
