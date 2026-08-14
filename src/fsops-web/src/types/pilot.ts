export type PilotStatus = 'Available' | 'Flying' | 'Inactive'

/**
 * A saved weekly pattern that has quietly stopped producing flights, because its aircraft is parked
 * at an airport no leg in the pattern departs from - so nothing in the schedule can ever move it
 * again. Not an error and not a validation failure: the schedule is still valid and still
 * repeating, and it resumes on its own the moment the aircraft is back. See ScheduleStallDetector
 * for why this is narrower than "the aircraft isn't where the week starts" (a pattern that visits
 * wherever the aircraft is standing repairs itself, and warning about it would be crying wolf).
 */
export interface ScheduleStall {
  fleetAircraftId: string
  registration: string
  /** Where the aircraft actually is. */
  locationIcao: string
  /** Where the earliest leg of the week departs from. */
  patternStartIcao: string
  /** Server-composed sentence: what is wrong, and the two ways out. */
  message: string
}

/**
 * One virtual pilot, as returned by GET /pilots. The trailing three fields are weekly
 * projections computed from the pilot's saved schedule - the backend may not populate them
 * yet ("may be null in the first cut"), so callers must treat their absence as "not known"
 * rather than zero.
 */
export interface PilotSummary {
  id: string
  name: string
  isPlayer: boolean
  monthlySalary: number
  hoursFlown: number
  /** Recomputed live on every fetch (see PilotEndpoints.ListAsync) - never stale between the
   *  resolver's periodic decay passes, so this is always the true-right-now figure. */
  skillRating: number
  /** What hoursFlown alone would give this pilot with no idle decay applied - lets the UI show
   *  "earned X, currently Y" when decay has pulled skillRating below it. */
  earnedSkillRating: number
  /** Null for the player pilot (their own record never decays) and for a virtual pilot who has
   *  never flown yet. */
  lastFlewUtc: string | null
  /** Days since lastFlewUtc, null on the same conditions as lastFlewUtc. */
  idleDays: number | null
  /** True once idle time has passed the grace period and skillRating is actively decaying. */
  isDecaying: boolean
  /** Days of grace left before decay starts, 0 once isDecaying is true. Null on the same
   *  conditions as lastFlewUtc. */
  decayGraceDaysRemaining: number | null
  /** Derived server-side on every fetch from this pilot's in-progress flight and schedule, never
   *  read from a stored column - see PilotStatusCalculator. */
  status: PilotStatus
  createdUtc: string
  sectorsPerWeek?: number | null
  weeklyEstimatedRevenue?: number | null
  weeklyEstimatedCost?: number | null
  /** Empty for the overwhelmingly common case. One entry per aircraft on this pilot's schedule that
   *  has stalled - see ScheduleStall. */
  scheduleStalls?: ScheduleStall[]
}

/** POST /pilots response: the new pilot plus the airline's cash balance after the hiring cost. */
export interface HirePilotResponse {
  pilot: PilotSummary
  cashBalance: number
}
