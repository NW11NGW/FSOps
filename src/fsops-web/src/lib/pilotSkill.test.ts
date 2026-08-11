import { describe, expect, it } from 'vitest'

import { pilotSkillStatus } from '@/lib/pilotSkill'
import type { PilotSummary } from '@/types/pilot'

function pilot(overrides: Partial<PilotSummary> = {}): PilotSummary {
  return {
    id: 'p1',
    name: 'First Officer 1',
    isPlayer: false,
    monthlySalary: 3000,
    hoursFlown: 120,
    skillRating: 62,
    earnedSkillRating: 62,
    lastFlewUtc: '2026-08-10T12:00:00Z',
    idleDays: 1,
    isDecaying: false,
    decayGraceDaysRemaining: 13,
    status: 'Available',
    createdUtc: '2026-08-01T00:00:00Z',
    ...overrides,
  }
}

describe('pilotSkillStatus', () => {
  it('says the player pilot never decays, regardless of any idle fields', () => {
    const result = pilotSkillStatus(pilot({ isPlayer: true, isDecaying: true, decayGraceDaysRemaining: 0 }))
    expect(result).toEqual({ tone: 'never-decays', message: 'Your own flying — skill never decays.' })
  })

  it('says a pilot who has never flown has not flown yet, not "idle since null"', () => {
    const result = pilotSkillStatus(pilot({ lastFlewUtc: null, idleDays: null, decayGraceDaysRemaining: null }))
    expect(result).toEqual({ tone: 'never-flown', message: 'Has not flown yet.' })
  })

  it('reports active decay with both the earned and current figures, not just the smaller number', () => {
    const result = pilotSkillStatus(
      pilot({ isDecaying: true, skillRating: 58, earnedSkillRating: 68, decayGraceDaysRemaining: 0 }),
    )
    expect(result.tone).toBe('decaying')
    expect(result.message).toContain('earned 68')
    expect(result.message).toContain('currently 58')
  })

  it('warns before decay starts once the grace window is closing, not only once it has bitten', () => {
    const result = pilotSkillStatus(pilot({ isDecaying: false, decayGraceDaysRemaining: 3 }))
    expect(result.tone).toBe('grace-warning')
    expect(result.message).toContain('decay starts in 3 days')
  })

  it('uses singular "day" for exactly one day of grace remaining', () => {
    const result = pilotSkillStatus(pilot({ isDecaying: false, decayGraceDaysRemaining: 0.4 }))
    expect(result.message).toContain('decay starts in 1 day ')
  })

  it('shows a plain "flew on" line with no warning when comfortably inside the grace period', () => {
    const result = pilotSkillStatus(pilot({ isDecaying: false, decayGraceDaysRemaining: 12 }))
    expect(result.tone).toBe('normal')
    expect(result.message).not.toContain('decay')
  })
})
