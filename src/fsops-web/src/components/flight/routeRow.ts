import type { FlightOptionAircraft } from '@/types/flight'

/**
 * Unified shape the route selector renders, whether it came from GET /flights/options (rich:
 * flyability + available aircraft) or the GET /routes fallback used when that endpoint isn't
 * deployed yet (plain: every route shown, aircraft assignment unknown).
 */
export interface RouteRow {
  routeId: string
  flightNumber: string | null
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  distanceNm: number
  blockMinutes: number | null
  baseFare: number
  isFlyable: boolean
  reason: string | null
  availableAircraft: FlightOptionAircraft[]
  /** True when this row came from the fallback path - aircraft availability genuinely isn't known, not just empty. */
  aircraftUnknown: boolean
}
