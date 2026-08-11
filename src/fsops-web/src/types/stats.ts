/**
 * GET /stats/performance?days= - on-time performance and load factor bucketed by completion day.
 * Backed entirely by posted Flight rows (see StatsEndpoints' own doc) - never a cached total.
 * `onTimePercent` mirrors the same delay/measurability rule the reputation card uses (Completed,
 * arrival time known, not SimRateElevated), so this chart can never disagree with it over the same
 * window. Both percentages are null (never a fabricated 0) for a day nothing could measure.
 */
export interface StatsPerformancePoint {
  dateUtc: string
  sectorsFlown: number
  onTimePercent: number | null
  loadFactorPercent: number | null
}

export interface StatsPerformanceResponse {
  periodDays: number
  points: StatsPerformancePoint[]
}

/**
 * GET /stats/fleet?days= - one row per fleet aircraft. `hoursFlownInPeriod`/`idleHoursInPeriod` are
 * summed from completed flights' own Out/In times for the requested window, never the lifetime
 * AirframeHours counter - see StatsEndpoints.FleetAsync's own doc. `hoursToNextACheck`/
 * `hoursToNextCCheck` mirror FleetEndpoints.ListAsync's own computation exactly.
 */
export interface StatsFleetAircraft {
  fleetAircraftId: string
  registration: string
  aircraftTypeName: string
  status: string
  sectorsFlown: number
  hoursFlownInPeriod: number
  idleHoursInPeriod: number
  utilisationPercent: number
  hoursSinceACheck: number
  hoursSinceCCheck: number
  hoursToNextACheck: number
  hoursToNextCCheck: number
  conditionPercent: number
}

export interface StatsFleetResponse {
  periodDays: number
  aircraft: StatsFleetAircraft[]
}

/**
 * GET /stats/pilots?days= - the logbook, one row per pilot including the player.
 * `hoursFlown`/`sectorsFlown` are summed from completed flights in the window, never the lifetime
 * Pilot.HoursFlown counter. `onTimePercent`/`averageLandingFpm` are null (never 0) when nothing in
 * the window could measure that signal honestly.
 */
export interface StatsPilotLogbookEntry {
  pilotId: string
  name: string
  isPlayer: boolean
  sectorsFlown: number
  hoursFlown: number
  onTimePercent: number | null
  averageLandingFpm: number | null
}

export interface StatsPilotsResponse {
  periodDays: number
  pilots: StatsPilotLogbookEntry[]
}
