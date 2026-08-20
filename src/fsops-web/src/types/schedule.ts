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

/** One entry in `LegalLegOption.warnings`. `severity` tells the picker how to weight it visually -
 *  it never affects whether the option is selectable (see `LegalLegOption.warnings` doc below).
 *  `"info"` is a consequence resolvable purely by continuing to build the week - a continuity gap,
 *  where this leg leaves the aircraft somewhere new and the very next leg the player adds is the
 *  fix, the ordinary halfway point of an ordinary round trip. `"alert"` is a genuine incompatibility
 *  with something already committed to elsewhere (double-booking, insufficient turnaround or rest,
 *  an over-long duty day) that adding this leg does not, by itself, resolve - fixing it means
 *  changing something else already on the calendar, not just continuing forward. Backend text,
 *  render `message` verbatim (same convention as `IllegalLegOption.reason`). */
export interface LegWarning {
  message: string
  severity: 'info' | 'alert'
}

/** A route that can legally depart at the queried day/time/aircraft. `blockMinutes` is computed
 *  against THIS specific aircraft (see PilotEndpoints.GetLegOptionsAsync) - the same figure a save
 *  will resolve to, never a route-level default from a different aircraft type. Null only if the
 *  backend's own lookup came up empty (e.g. world data gap) - callers should fall back the same way
 *  a saved leg's `ScheduleLeg.blockMinutes` does.
 *
 *  `warnings` - real-use defect fix, 2026-08-12: this leg is genuinely selectable (nothing already
 *  committed BEFORE this slot rules it out), but picking it creates a consequence against something
 *  already drafted LATER in the week - e.g. the aircraft won't be back in position for a day that's
 *  already been built. That's not a reason to refuse it (the player can resolve it with their very
 *  next leg), so it stays legal - the warning is shown, never hidden, and never blocks the pick.
 *  Empty for the common case where the leg has no such consequence. Ordered with `"alert"`-severity
 *  entries first (2026-08-13: the continuity-gap case used to share amber styling with genuine
 *  incompatibilities and fire on every ordinary first leg of a round trip, teaching players to
 *  ignore the colour) - the picker only ever renders `warnings[0]`, so the more urgent kind leads. */
export interface LegalLegOption {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  blockMinutes: number | null
  /** What ONE sector on this route is expected to net, in base currency, flown by the specific
   *  aircraft this query named - the backend's own economy engine (the same one that posts to the
   *  ledger), never a client-side estimate. Aircraft-specific on purpose: seats, MTOW and this
   *  airframe's block time all feed it, so the same city pair is worth different money to
   *  different aircraft. Informational only - it never affects whether an option is legal, only
   *  how a caller ranks options that already are. Absent/null when the backend could not resolve a
   *  figure (a world-data gap, or a zero-distance route); callers must fall back to ordering by
   *  something else rather than treating it as zero. */
  expectedNetProfit?: number | null
  /** How many legs OTHER pilots already fly on this route in the saved week. A city pair's market
   *  is finite, so this is the honest signal that putting a second pilot on the same pair is worth
   *  less than the first. Never blocking, and never counts the caller's own draft. */
  scheduledLegsThisWeek?: number
  warnings: LegWarning[]
}

/** A route considered and rejected for the queried day/time/aircraft, with the backend's reason -
 *  always shown, never silently dropped. Already worded and ordered by the backend (a hard
 *  physical blocker outranks reservation) - render verbatim, never re-derive or re-order. Unlike
 *  `LegalLegOption.warnings`, this is a genuine, permanent disqualifier for this slot (a conflict
 *  with something already committed BEFORE it) - nothing the player does with a later leg changes it. */
export interface IllegalLegOption {
  routeId: string
  /** Which sector this refusal is about. Optional only because an older server did not send it -
   *  every current response does. It exists so a caller that asked for a specific sector can find
   *  ITS refusal in a list covering every route the airline has, rather than guessing from list
   *  order: see PilotEndpoints.GetLegOptionsAsync, and starterSchedule.ts's `describeRefusal` for
   *  the caller that needs it. */
  departureIcao?: string
  arrivalIcao?: string
  reason: string
}

/** The duty, rest and turnaround limits the backend actually validates this airline against, so a
 *  client never keeps a second copy of them (see PilotEndpoints.SchedulingLimitsOf). Optional on the
 *  wire only for an older server; callers fall back to the shipped defaults rather than failing. */
export interface SchedulingLimits {
  maxDutyHoursPerDay: number
  minRestHoursBetweenDutyDays: number
  minTurnaroundMinutes: number
}

export interface LegOptionsResponse {
  legal: LegalLegOption[]
  illegal: IllegalLegOption[]
  /** See {@link SchedulingLimits}. */
  scheduling?: SchedulingLimits
  /** Where the aircraft actually is once this slot comes around, resolved from whatever's already
   *  committed before it (or its recorded location, if nothing precedes it this week yet) -
   *  informational only, never used to filter which routes are tested (see the backend's own
   *  remarks on PilotEndpoints.GetLegOptionsAsync: a route that doesn't depart from here still gets
   *  tested and, if illegal, still shown with its own reason - this is just for leading copy like
   *  "G-TXFE is at EGGD"). Absent when the aircraft is grounded or reserved (those responses explain
   *  themselves without needing a position). */
  aircraftPosition?: string
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
