/** .NET `DayOfWeek` numbering: 0 = Sunday ... 6 = Saturday. Every day-of-week value in this
 *  feature (API payloads, UI state) uses this numbering so it round-trips with the backend
 *  without translation. */
export type DayOfWeek = 0 | 1 | 2 | 3 | 4 | 5 | 6

/** Monday-first display order for the calendar grid - purely a UI ordering choice, the
 *  underlying DayOfWeek values are unchanged. */
export const DAY_DISPLAY_ORDER: DayOfWeek[] = [1, 2, 3, 4, 5, 6, 0]

export const DAY_LABELS: Record<DayOfWeek, string> = {
  0: 'Sunday',
  1: 'Monday',
  2: 'Tuesday',
  3: 'Wednesday',
  4: 'Thursday',
  5: 'Friday',
  6: 'Saturday',
}

export const DAY_SHORT_LABELS: Record<DayOfWeek, string> = {
  0: 'Sun',
  1: 'Mon',
  2: 'Tue',
  3: 'Wed',
  4: 'Thu',
  5: 'Fri',
  6: 'Sat',
}

/** One leg on a pilot's standing weekly schedule, as returned by GET/PUT /pilots/{id}/schedule.
 *  `departureTimeUtc` is "HH:mm:ss". `blockMinutes` is the full gate-to-gate duration (preflight,
 *  taxi, flight, taxi in) - this is what sizes the block on the calendar, not airborne time alone. */
export interface ScheduleEntry {
  id: string
  dayOfWeek: DayOfWeek
  departureTimeUtc: string
  routeId: string
  fleetAircraftId: string
  blockMinutes: number
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
}

export interface PilotSchedule {
  pilotId: string
  entries: ScheduleEntry[]
}

/** The subset of ScheduleEntry the PUT body actually needs - blockMinutes/icaos/flightNumber are
 *  derived server-side from routeId, never sent by the client. */
export interface ScheduleEntryInput {
  dayOfWeek: DayOfWeek
  departureTimeUtc: string
  routeId: string
  fleetAircraftId: string
}

export interface SaveScheduleRequest {
  entries: ScheduleEntryInput[]
}

/** PUT /pilots/{id}/schedule's 400 response. `conflicts` are complete, human-readable sentences
 *  written by the backend - render them verbatim, never re-worded or truncated. */
export interface ScheduleConflictResponse {
  error: string
  conflicts: string[]
}

/** A route + aircraft combination that can legally depart at the queried day/time. */
export interface LegalScheduleOption {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  fleetAircraftId: string
  registration: string
}

/** A route + aircraft combination that was considered and rejected for the queried day/time,
 *  with the backend's reason for why - always shown, never silently dropped. */
export interface IllegalScheduleOption {
  routeId: string
  fleetAircraftId: string
  reason: string
}

export interface ScheduleOptionsResponse {
  legal: LegalScheduleOption[]
  illegal: IllegalScheduleOption[]
}

/**
 * POST /pilots/{id}/schedule/options body. `draftEntries` is this pilot's on-screen draft (same
 * shape as PUT /schedule's `entries`) - it replaces what the server would otherwise read from the
 * database for this pilot, so a candidate is judged against what's actually been built on screen,
 * saved or not. Week-closure (the wraparound from the last leg back to the first) is intentionally
 * NOT required here - only PUT /schedule enforces that, once the whole week is submitted.
 */
export interface ScheduleOptionsRequest {
  day: DayOfWeek
  time: string
  draftEntries?: ScheduleEntryInput[] | null
}

/**
 * The subset of GET /fleet's fields the schedule builder needs to show "which aircraft does this
 * entry use" and "which aircraft is off-limits and why". Defined locally rather than imported
 * from types/fleet.ts (owned by another agent) so this feature does not depend on that file
 * gaining the `reservedForPlayer` field on the same timeline.
 */
export interface FleetAircraftLite {
  id: string
  registration: string
  aircraftTypeName: string
  family: string
  paxCapacity: number
  ownership: 'Owned' | 'Leased'
  status: string
  locationIcao: string
  airframeHours: number
  hoursToNextACheck: number
  hoursToNextCCheck: number
  conditionPercent: number
  fuelOnBoardKg: number
  groundedUntilUtc: string | null
  groundedReason: string | null
  reservedForPlayer: boolean
}

/** "08:30:00" -> 510 (minutes since midnight). Tolerant of a missing seconds component. */
export function timeToMinutes(time: string): number {
  const [h, m] = time.split(':')
  const hours = Number(h) || 0
  const minutes = Number(m) || 0
  return hours * 60 + minutes
}

/** 510 -> "08:30:00", wrapping into 0-23h - callers are responsible for carrying a day overflow. */
export function minutesToTime(totalMinutes: number): string {
  const wrapped = ((totalMinutes % 1440) + 1440) % 1440
  const hours = Math.floor(wrapped / 60)
  const minutes = wrapped % 60
  return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:00`
}
