import type { FlightOptionAircraft } from '@/types/flight'

/**
 * A `FlightOptionAircraft` enriched with `icaoType`/`family` joined client-side from
 * GET /fleet/aircraft-types by `aircraftTypeId` - GET /flights/options itself doesn't carry
 * these (see types/flight.ts), but the SimBrief hand-off and the sim-aircraft readiness check
 * both need an ICAO type code to compare against. Falls back to the aircraft's own type NAME when
 * the catalogue hasn't loaded yet or the id doesn't match anything, so those two features degrade
 * gracefully rather than breaking.
 */
export interface AircraftOptionRow extends FlightOptionAircraft {
  icaoType: string
  family: string
}

/**
 * Unified shape the route selector renders, whether it came from GET /flights/options (rich:
 * flyability + every aircraft physically at the departure airport) or the GET /routes fallback
 * used when that endpoint isn't deployed yet (plain: every route shown, aircraft availability
 * unknown).
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
  /** Every aircraft physically at the departure airport - flyable or not, never omitted (see
   *  types/flight.ts's FlightOption doc). Empty when `aircraftUnknown` is true (the fallback
   *  path) or when GET /flights/options itself says nothing is there. */
  aircraftOptions: AircraftOptionRow[]
  /** True when this row came from the fallback path - aircraft availability genuinely isn't known, not just empty. */
  aircraftUnknown: boolean
}

/**
 * One round-trip pair as the Fly screen shows it: a single entry offering whichever direction can
 * actually be flown right now, instead of two rows where one is always noise (the aircraft can
 * only be at one end of the pair).
 */
export interface RoutePairRow {
  /** Stable identity for this pair - the routeId of whichever leg is encountered first. */
  pairId: string
  /** The direction currently offered: the flyable leg, preferring the leg matching `header`'s
   *  departure->arrival order when both (or neither) are flyable. This is the leg that Start
   *  flight, the flight brief, and the preview all act on. */
  active: RouteRow
  /** The reverse leg, when the server has paired one. Null only in the brief moment right after a
   *  legacy unpaired route is created, before the server self-heals the pair. */
  other: RouteRow | null
  /** True when the offered direction is `other` rather than the "header order" leg - used to
   *  label it "Return available" instead of "Outbound available". */
  activeIsReturn: boolean
  /** True when either direction can be flown right now. */
  isFlyable: boolean
  /** Set only when isFlyable is false - why neither direction works. */
  reason: string | null
  /** Header departure/arrival, in a fixed order regardless of which direction is offered, so the
   *  pair's identity ("EGGD <-> EGPH") doesn't flip depending on where the aircraft happens to be. */
  headerDepartureIcao: string
  headerArrivalIcao: string
}

function buildPair(header: RouteRow, other: RouteRow | null): RoutePairRow {
  const headerFlyable = header.isFlyable
  const otherFlyable = other?.isFlyable ?? false

  const activeIsReturn = !headerFlyable && otherFlyable
  const active = activeIsReturn && other ? other : header

  return {
    pairId: header.routeId,
    active,
    other,
    activeIsReturn,
    isFlyable: headerFlyable || otherFlyable,
    reason: headerFlyable || otherFlyable ? null : (header.reason ?? other?.reason ?? null),
    headerDepartureIcao: header.departureIcao,
    headerArrivalIcao: header.arrivalIcao,
  }
}

/**
 * Pairs up outbound/return legs by returnRouteId (mirrors RoutesTable's groupRoutePairs, kept
 * separate since this operates on RouteRow - options-joined data - rather than raw RouteSummary).
 * `rows` should arrive in a stable order (GET /flights/options and GET /routes both return routes
 * ordered by creation time) so the leg encountered first consistently anchors the pair's header.
 */
export function pairRouteRows(rows: RouteRow[], returnRouteIdByRouteId: Record<string, string | null | undefined>): RoutePairRow[] {
  const byId = new Map(rows.map((row) => [row.routeId, row]))
  const seen = new Set<string>()
  const pairs: RoutePairRow[] = []

  for (const row of rows) {
    if (seen.has(row.routeId)) continue
    seen.add(row.routeId)

    const returnId = returnRouteIdByRouteId[row.routeId] ?? null
    const partner = returnId ? byId.get(returnId) : undefined
    const other = partner && !seen.has(partner.routeId) ? partner : null
    if (other) seen.add(other.routeId)

    pairs.push(buildPair(row, other))
  }

  return pairs
}
