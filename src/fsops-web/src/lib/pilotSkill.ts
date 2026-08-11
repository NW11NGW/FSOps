import { formatDate } from '@/lib/format'
import type { PilotSummary } from '@/types/pilot'

export type PilotSkillTone = 'never-decays' | 'never-flown' | 'decaying' | 'grace-warning' | 'normal'

export interface PilotSkillStatus {
  tone: PilotSkillTone
  message: string
}

/** Once grace-remaining days fall to this or below (and decay hasn't started yet), the status
 *  switches from a plain "flew on X" line to an explicit countdown - docs/PLAN.md "Idle decay must
 *  be visible and understandable before it bites": the warning has to land before the number
 *  actually starts moving, not only once it has. */
const GRACE_WARNING_DAYS = 5

/**
 * One line describing a pilot's idle/decay state in plain language, for the Pilots roster - see
 * PilotSummary's own fields (all computed server-side by PilotEndpoints.ListAsync). Pure and
 * side-effect free so it's independently testable from any component that renders it.
 */
export function pilotSkillStatus(pilot: PilotSummary): PilotSkillStatus {
  if (pilot.isPlayer) {
    return { tone: 'never-decays', message: 'Your own flying — skill never decays.' }
  }

  if (!pilot.lastFlewUtc) {
    return { tone: 'never-flown', message: 'Has not flown yet.' }
  }

  const lastFlew = formatDate(pilot.lastFlewUtc)

  if (pilot.isDecaying) {
    return {
      tone: 'decaying',
      message: `Idle since ${lastFlew} — skill is decaying (earned ${pilot.earnedSkillRating.toFixed(0)}, currently ${pilot.skillRating.toFixed(0)}).`,
    }
  }

  if (pilot.decayGraceDaysRemaining != null && pilot.decayGraceDaysRemaining <= GRACE_WARNING_DAYS) {
    const daysRemaining = Math.max(1, Math.ceil(pilot.decayGraceDaysRemaining))
    const dayWord = daysRemaining === 1 ? 'day' : 'days'
    return {
      tone: 'grace-warning',
      message: `Flew ${lastFlew} — decay starts in ${daysRemaining} ${dayWord} if still idle.`,
    }
  }

  return { tone: 'normal', message: `Flew ${lastFlew}.` }
}
