export type PilotStatus = 'Available' | 'Flying' | 'Inactive'

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
  status: PilotStatus
  createdUtc: string
  sectorsPerWeek?: number | null
  weeklyEstimatedRevenue?: number | null
  weeklyEstimatedCost?: number | null
}

/** POST /pilots request body - name is optional, the backend assigns a generated one when omitted. */
export interface HirePilotRequest {
  name?: string
}

/** POST /pilots response: the new pilot plus the airline's cash balance after the hiring cost. */
export interface HirePilotResponse {
  pilot: PilotSummary
  cashBalance: number
}
