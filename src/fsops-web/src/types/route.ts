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

export interface RoutePreviewResponse {
  distanceNm: number
  initialBearingDeg: number
  estimatedBlockMinutes: number
  blockTimeBreakdown: BlockTimeBreakdown | null
  cruiseAltitudeFt: number
  blockFuelKg: number
  fuelBreakdown: FuelBreakdown | null
  suggestedFare: number
  greatCirclePath: LonLat[]
  validation: RoutePreviewValidation
}

export interface RoutePreviewRequest {
  departureIcao: string
  arrivalIcao: string
  aircraftTypeId?: string
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
