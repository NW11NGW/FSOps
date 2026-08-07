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
 * Documented POST /routes contract. Note: the live backend's CreateRouteRequest record only
 * binds departureIcao/arrivalIcao/aircraftTypeId - baseFare is accepted here (and sent) per the
 * documented contract, but is currently ignored server-side, which always prices the route at
 * RoutePreviewCalculator's suggested fare. See the route builder's fare override control.
 */
export interface CreateRouteRequest {
  departureIcao: string
  arrivalIcao: string
  baseFare?: number
}

export interface RouteSummary {
  id: string
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  distanceNm: number
  baseFare: number
  isActive: boolean
  createdUtc: string
}
