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

/**
 * One leg inside a duty day, as returned by GET/PUT /pilots/{id}/schedule. `departureTimeUtc` is
 * "HH:mm:ss". `blockMinutes` is the full gate-to-gate duration (preflight, taxi, flight, taxi in)
 * - this is what sizes the block on the calendar, not airborne time alone. There is deliberately
 * no `fleetAircraftId` here - the aircraft belongs to the whole `ScheduleDutyDay`, never the leg
 * - with one airframe fixed for the day, "does the next leg depart where the last one arrived"
 * is a single check against one aircraft's position, and a turnaround gap always means what it says.
 */
export interface ScheduleLeg {
  id: string
  departureTimeUtc: string
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  blockMinutes: number | null
}

/** One duty day: the ONE aircraft chosen for it, then its legs in departure order. */
export interface ScheduleDutyDay {
  dayOfWeek: DayOfWeek
  fleetAircraftId: string
  registration: string | null
  legs: ScheduleLeg[]
}

/** GET/PUT /pilots/{id}/schedule response. A pilot with no saved schedule has `dutyDays: []` and
 *  `autoSuspendOnMaintenance: true` (the same default a first save applies - see
 *  SaveScheduleRequest). */
export interface PilotSchedule {
  pilotId: string
  dutyDays: ScheduleDutyDay[]
  /** Property of the WHOLE weekly schedule, not any one day or leg. When true
   *  (the default), this schedule pauses while its aircraft is grounded for a maintenance check
   *  and resumes automatically once the check finishes. When false, every occurrence scheduled
   *  during that grounding is cancelled instead - at a real cancellation fee under True-life. */
  autoSuspendOnMaintenance: boolean
}

/** One leg inside a PUT request's duty day - carries no aircraft field of its own. */
export interface DutyLegInput {
  departureTimeUtc: string
  routeId: string
}

/** One duty day as sent to PUT /pilots/{id}/schedule or POST .../leg-options'
 *  `draftDutyDays`. `fleetAircraftId` is required whenever `legs` is non-empty. */
export interface DutyDayInput {
  dayOfWeek: DayOfWeek
  fleetAircraftId: string | null
  legs: DutyLegInput[]
}

/** PUT /pilots/{id}/schedule request. `autoSuspendOnMaintenance` is technically optional on the
 *  wire (an omitted value resets to `true` server-side) but every caller in this app must send it
 *  explicitly once it has a value to send - see useSchedule.ts's `save`. Omitting it on an
 *  unrelated save would silently flip a player's setting back on. */
export interface SaveScheduleRequest {
  dutyDays: DutyDayInput[]
  autoSuspendOnMaintenance: boolean
}

/** PUT /pilots/{id}/schedule's 400 response. `conflicts` are complete, human-readable sentences
 *  written by the backend - render them verbatim, never re-worded or truncated. */
export interface ScheduleConflictResponse {
  error: string
  conflicts: string[]
}

/** Body for POST /pilots/{id}/schedule/aircraft-options - step one of the picker. */
export interface AircraftOptionsRequest {
  day: DayOfWeek
}

/** One fleet aircraft's eligibility for a given duty day - step one of the picker
 *  - the aircraft is picked first, then the leg. Not a full conflict check: only the aircraft's
 *  own state (reserved / grounded) is screened here; `leg-options` checks real conflicts once an
 *  aircraft is fixed. */
export interface AircraftOption {
  fleetAircraftId: string
  registration: string
  aircraftTypeName: string
  /** The aircraft's current real-world position - informational, not a guarantee about where it
   *  will be at the start of THIS duty day; leg-options validates that once legs are added. */
  locationIcao: string
  eligible: boolean
  reason: string | null
  /** This aircraft's SAVED legs across the whole airline this week - informational idle-capacity
   *  signal, never blocking (an aircraft can legitimately serve more than one pilot's day). */
  scheduledLegsThisWeek: number
}

export interface AircraftOptionsResponse {
  options: AircraftOption[]
}

/** Body for POST /pilots/{id}/schedule/leg-options - step two of the picker. */
export interface LegOptionsRequest {
  day: DayOfWeek
  /** "HH:mm" */
  time: string
  fleetAircraftId: string
  draftDutyDays?: DutyDayInput[] | null
}

/** A route that can legally depart at the queried day/time/aircraft. */
export interface LegalLegOption {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
}

/** A route considered and rejected for the queried day/time/aircraft, with the backend's reason -
 *  always shown, never silently dropped. Already worded and ordered by the backend (a hard
 *  physical blocker outranks reservation) - render verbatim, never re-derive or re-order. */
export interface IllegalLegOption {
  routeId: string
  reason: string
}

export interface LegOptionsResponse {
  legal: LegalLegOption[]
  illegal: IllegalLegOption[]
}

/** One leg as it appears in GET /pilots/schedule/overview - carries pilot identity so the
 *  by-aircraft view can colour-code each leg by whose duty day it belongs to. */
export interface OverviewLeg {
  fleetAircraftId: string
  pilotId: string
  pilotName: string | null
  dayOfWeek: DayOfWeek
  departureTimeUtc: string
  routeId: string
  departureIcao: string | null
  arrivalIcao: string | null
  flightNumber: string | null
}

export interface OverviewAircraftRow {
  fleetAircraftId: string
  registration: string
  locationIcao: string
  legs: OverviewLeg[]
}

export interface OverviewPilotDutyDay {
  dayOfWeek: DayOfWeek
  fleetAircraftId: string
  registration: string | null
  legs: OverviewLeg[]
}

export interface OverviewPilotRow {
  pilotId: string
  name: string
  dutyDays: OverviewPilotDutyDay[]
}

/** GET /pilots/schedule/overview response - read-only, airline-wide, serves both toggle states
 *  (by aircraft / by pilot) of the same underlying week in one call. Virtual pilots only - the
 *  player has no standing schedule. */
export interface ScheduleOverviewResponse {
  byAircraft: OverviewAircraftRow[]
  byPilot: OverviewPilotRow[]
}

/**
 * The subset of GET /fleet's fields the schedule builder needs to show "which aircraft is off
 * limits and why" in supplementary panels. Defined locally rather than imported from
 * types/fleet.ts so the builder depends only on the handful of fields it actually reads, and does
 * not break when the fleet DTO grows fields it has no use for.
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
