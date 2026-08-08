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
  skillRating: number
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
