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

/**
 * `onlineEligibleSectorsFlown` is the count of sectors where VATSIM online detection actually ran
 * (Flight.VatsimOnline is non-null); `onlineSectorsFlown` is how many of those matched online.
 * Both are over the whole window, not bucketed by day. A flight FSOps never checked - no CID
 * configured, the feature off, or the feed unreachable - is excluded from both counts rather than
 * folded into "not online": null is "unknown", not "no".
 */
export interface StatsPerformanceResponse {
  periodDays: number
  points: StatsPerformancePoint[]
  onlineSectorsFlown: number
  onlineEligibleSectorsFlown: number
}

/**
 * One day of GET /stats/trends?days= - the airline's direction of travel.
 *
 * Every field is derived from rows that already exist; nothing here is a snapshot of a running
 * total kept on the side. A field that is null means "not measured that day", never zero.
 */
export interface StatsTrendPoint {
  /** UTC calendar day, `yyyy-MM-dd`. */
  dateUtc: string
  /** Cash balance at the end of this day, in base units. Exact: the ledger is append-only and the
   *  app defines cash as the sum of every transaction, so a running total up to a day IS the
   *  balance on that day. Present for every day in the window, including quiet ones. */
  cashBalance: number
  sectorsFlown: number
  /** Same per-day rule as the performance chart - they share one server-side implementation. */
  onTimePercent: number | null
  loadFactorPercent: number | null
  /** The airline's genuinely RECORDED reputation for this day, from the insert-only daily snapshot.
   *  Null for any day the app was not open to observe it - never carried forward from the previous
   *  day, because a score FSOps never observed is not one it may claim. Necessarily null for every
   *  day before snapshotting shipped. */
  reputation: number | null
  /** The average score this day's sectors were pulling reputation TOWARD - not reputation itself.
   *  This is the exact quantity the dashboard's reputation card already averages to decide whether
   *  it reads "improving", "steady" or "declining", so the two can never disagree; its value is
   *  that it works retroactively over history flown long before snapshots existed. Null on a day
   *  whose sectors carried no measurable on-time or landing signal at all. */
  reputationPressure: number | null
}

export interface StatsTrendsResponse {
  periodDays: number
  points: StatsTrendPoint[]
  /** The airline's live reputation score right now - drawn as a reference so the pressure series
   *  can be read against where reputation actually stands. Null only when there is no airline. */
  currentReputation: number | null
  /** How many days in this window have a genuinely recorded reputation. Lets the UI say plainly
   *  whether the recorded line is a real series yet or is still filling up. */
  reputationRecordedDays: number
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
