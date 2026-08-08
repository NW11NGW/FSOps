import type { FleetAircraftLite, IllegalScheduleOption, LegalScheduleOption, ScheduleOptionsResponse } from '@/types/schedule'

/**
 * One aircraft's worth of what POST /schedule/options said about this slot - the unit the picker
 * is organised around (see docs/PLAN.md "2b. The unavailable list must not be a wall of text",
 * user feedback 2026-08-08: a flat list of route x aircraft pairs is a wall of text; picking the
 * aircraft first, then seeing only that aircraft's legs, is what makes it scannable).
 */
export interface AircraftOptionGroup {
  fleetAircraftId: string
  registration: string
  /** A setting the player chose, not a scheduling conflict - never rendered per-route. */
  reserved: boolean
  legal: LegalScheduleOption[]
  /** Every reason this aircraft can't fly a given route right now - each backend sentence kept
   *  verbatim (never re-worded here), one per route. Empty for a reserved aircraft: repeating
   *  "reserved for the player" once per route is exactly the wall-of-text problem this exists to
   *  avoid, so that reason collapses to the single `reserved` flag instead. */
  illegal: { routeId: string; reason: string }[]
}

/**
 * Groups the backend's flat legal/illegal option lists by aircraft. Ordered so the most useful
 * aircraft leads: anything with a leg available first, then anything blocked but not reserved,
 * then reserved aircraft last (they're a quiet aside, not something to lead with), each group
 * alphabetised by registration.
 */
export function groupOptionsByAircraft(
  options: ScheduleOptionsResponse,
  fleetById: Map<string, FleetAircraftLite>,
): AircraftOptionGroup[] {
  const byAircraft = new Map<string, AircraftOptionGroup>()

  function ensure(fleetAircraftId: string, fallbackRegistration: string): AircraftOptionGroup {
    let group = byAircraft.get(fleetAircraftId)
    if (!group) {
      const aircraft = fleetById.get(fleetAircraftId)
      group = {
        fleetAircraftId,
        registration: aircraft?.registration ?? fallbackRegistration,
        reserved: aircraft?.reservedForPlayer ?? false,
        legal: [],
        illegal: [],
      }
      byAircraft.set(fleetAircraftId, group)
    }
    return group
  }

  for (const option of options.legal) {
    ensure(option.fleetAircraftId, option.registration).legal.push(option)
  }

  for (const option of options.illegal) {
    const group = ensure(option.fleetAircraftId, 'Unknown aircraft')
    if (group.reserved) continue // said once via the `reserved` flag, never per route
    group.illegal.push({ routeId: option.routeId, reason: option.reason })
  }

  return [...byAircraft.values()].sort((a, b) => {
    const rank = (g: AircraftOptionGroup) => (g.legal.length > 0 ? 0 : g.reserved ? 2 : 1)
    const rankDiff = rank(a) - rank(b)
    return rankDiff !== 0 ? rankDiff : a.registration.localeCompare(b.registration)
  })
}

/** The single most relevant reason to headline a blocked aircraft's collapsed row with - just the
 *  first one the backend returned, kept verbatim; the rest are only ever a click away. */
export function headlineReason(group: AircraftOptionGroup): IllegalScheduleOption['reason'] | null {
  return group.illegal[0]?.reason ?? null
}
