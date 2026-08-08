import type { RouteLike } from './types'
import { timeToMinutes } from '@/types/schedule'
import type {
  DayOfWeek,
  FleetAircraftLite,
  LegalScheduleOption,
  ScheduleEntry,
  ScheduleEntryInput,
} from '@/types/schedule'

/**
 * The grid's working copy of a schedule entry. Loaded entries and not-yet-saved additions share
 * this shape so the grid never needs to special-case "real" vs "pending" blocks - only `isNew`
 * (for styling) and `blockMinutes`'s provenance differ. A brand-new entry's blockMinutes comes
 * from the route's `estimatedBlockMinutes` (a real, backend-computed figure, just not yet
 * resolved against this exact aircraft) until the next successful save replaces it with the
 * server's authoritative per-entry value.
 */
export interface DraftEntry {
  /** Server id for a loaded entry, or a client-generated `draft-*` id for a pending addition -
   *  either way, stable for the life of this editing session so React keys and drag handles work. */
  id: string
  dayOfWeek: DayOfWeek
  departureTimeUtc: string
  routeId: string
  fleetAircraftId: string
  blockMinutes: number
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  registration: string
  isNew: boolean
}

let draftCounter = 0
export function nextDraftId(): string {
  draftCounter += 1
  return `draft-${Date.now()}-${draftCounter}`
}

export function entriesToDraft(entries: ScheduleEntry[], fleetById: Map<string, FleetAircraftLite>): DraftEntry[] {
  return entries.map((entry) => ({
    id: entry.id,
    dayOfWeek: entry.dayOfWeek,
    departureTimeUtc: entry.departureTimeUtc,
    routeId: entry.routeId,
    fleetAircraftId: entry.fleetAircraftId,
    blockMinutes: entry.blockMinutes,
    departureIcao: entry.departureIcao,
    arrivalIcao: entry.arrivalIcao,
    flightNumber: entry.flightNumber,
    registration: fleetById.get(entry.fleetAircraftId)?.registration ?? '—',
    isNew: false,
  }))
}

export function draftToScheduleInputs(draft: DraftEntry[]): ScheduleEntryInput[] {
  return draft.map((entry) => ({
    dayOfWeek: entry.dayOfWeek,
    departureTimeUtc: entry.departureTimeUtc,
    routeId: entry.routeId,
    fleetAircraftId: entry.fleetAircraftId,
  }))
}

/** Builds a new draft entry from a chosen legal option. `route` supplies the estimated block
 *  time and flight number the options endpoint doesn't carry. */
export function draftFromOption(
  option: LegalScheduleOption,
  day: DayOfWeek,
  time: string,
  route: RouteLike | undefined,
): DraftEntry {
  return {
    id: nextDraftId(),
    dayOfWeek: day,
    departureTimeUtc: time.length === 5 ? `${time}:00` : time,
    routeId: option.routeId,
    fleetAircraftId: option.fleetAircraftId,
    blockMinutes: route?.estimatedBlockMinutes ?? 60,
    departureIcao: option.departureIcao,
    arrivalIcao: option.arrivalIcao,
    flightNumber: route?.flightNumber ?? null,
    registration: option.registration,
    isNew: true,
  }
}

/** True if [startA, startA+lenA) and [startB, startB+lenB) overlap, treating minute ranges as
 *  half-open so a leg landing exactly when another departs does not count as an overlap. */
function rangesOverlap(startA: number, lenA: number, startB: number, lenB: number): boolean {
  return startA < startB + lenB && startB < startA + lenA
}

/** Any other draft entry on the same day whose time range overlaps this one - a pilot cannot be
 *  on two legs at once regardless of aircraft, so this is checked client-side before the slower
 *  round trip to the backend's full conflict check on save. */
export function findOverlappingEntry(
  candidate: { dayOfWeek: DayOfWeek; departureTimeUtc: string; blockMinutes: number },
  entries: DraftEntry[],
  excludeId?: string,
): DraftEntry | null {
  const start = timeToMinutes(candidate.departureTimeUtc)
  for (const entry of entries) {
    if (entry.id === excludeId) continue
    if (entry.dayOfWeek !== candidate.dayOfWeek) continue
    const entryStart = timeToMinutes(entry.departureTimeUtc)
    if (rangesOverlap(start, candidate.blockMinutes, entryStart, entry.blockMinutes)) return entry
  }
  return null
}
