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

export interface RoutePreviewValidation {
  withinRange: boolean
  departureRunwayAdequate: boolean
  arrivalRunwayAdequate: boolean
  sameAirport: boolean
  warnings: string[]
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
}

export interface RoutePreviewResponse {
  distanceNm: number
  initialBearingDeg: number
  estimatedBlockMinutes: number
  blockTimeBreakdown: BlockTimeBreakdown | null
  cruiseAltitudeFt: number
  blockFuelKg: number
  fuelBreakdown: FuelBreakdown | null
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
   *  falls back to the suggested fare server-side - no validation beyond that, per
   *  docs/PLAN.md "Fare setting and demand response": the simulation is the guardrail. */
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
