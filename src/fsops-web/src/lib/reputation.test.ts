import { describe, expect, it } from 'vitest'

import { reputationDemandLabel, reputationDrivers, reputationTrendLabel } from '@/lib/reputation'
import type { ReputationSummary } from '@/types/airline'

function summary(overrides: Partial<ReputationSummary> = {}): ReputationSummary {
  return {
    score: 62,
    direction: 'steady',
    sectorsConsidered: 10,
    onTimePercent: 80,
    cancelledCount: 0,
    skippedCount: 0,
    landingQuality: 'smooth',
    demandMultiplier: 1.05,
    ...overrides,
  }
}

describe('reputationTrendLabel', () => {
  it('maps improving to an up trend', () => {
    expect(reputationTrendLabel('improving')).toEqual({ direction: 'up', label: 'Improving' })
  })

  it('maps declining to a down trend', () => {
    expect(reputationTrendLabel('declining')).toEqual({ direction: 'down', label: 'Declining' })
  })

  it('maps steady to a flat trend', () => {
    expect(reputationTrendLabel('steady')).toEqual({ direction: 'flat', label: 'Steady' })
  })

  it('maps a brand-new airline (no history) to a flat trend with its own label, not "Steady"', () => {
    // A fresh airline has no evidence either way - claiming "Steady" would imply a flat trend it
    // has never actually shown.
    expect(reputationTrendLabel('new')).toEqual({ direction: 'flat', label: 'No history yet' })
  })
})

describe('reputationDrivers', () => {
  it('reports zero sectors as an honest "no history" line, not a driver with no evidence', () => {
    expect(reputationDrivers(summary({ sectorsConsidered: 0, onTimePercent: null, landingQuality: null }))).toEqual([
      'No completed sectors yet — your first flights start building a track record.',
    ])
  })

  it('combines on-time, disruption, and landing drivers when all are measurable', () => {
    const drivers = reputationDrivers(
      summary({ sectorsConsidered: 12, onTimePercent: 75, cancelledCount: 1, skippedCount: 2, landingQuality: 'firm' }),
    )
    expect(drivers).toEqual([
      '75% on time over your last 12 sectors.',
      '1 cancelled, 2 skipped in that window.',
      'Landings have been firm recently.',
    ])
  })

  it('never claims 0% on-time when the signal was simply unmeasurable', () => {
    // onTimePercent: null means "couldn't be measured", not "measured at zero" - a summary with no
    // measurable signals at all must not silently render "0% on time".
    const drivers = reputationDrivers(summary({ onTimePercent: null, landingQuality: null }))
    expect(drivers.join(' ')).not.toContain('0%')
    expect(drivers).toEqual(['Not enough measured sectors yet to say what is driving this.'])
  })

  it('omits the disruption line entirely when nothing was cancelled or skipped', () => {
    const drivers = reputationDrivers(summary({ cancelledCount: 0, skippedCount: 0 }))
    expect(drivers.some((d) => d.includes('cancelled') || d.includes('skipped'))).toBe(false)
  })

  it('uses singular "sector" for a window of exactly one', () => {
    const drivers = reputationDrivers(summary({ sectorsConsidered: 1, onTimePercent: 100 }))
    expect(drivers[0]).toBe('100% on time over your last 1 sector.')
  })
})

describe('reputationDemandLabel', () => {
  it('shows a positive uplift with an explicit + sign', () => {
    expect(reputationDemandLabel(1.08)).toBe('+8% passenger demand from reputation.')
  })

  it('shows a negative impact without a double sign', () => {
    expect(reputationDemandLabel(0.92)).toBe('-8% passenger demand from reputation.')
  })

  it('describes exactly 1.0 as baseline rather than "+0%"', () => {
    expect(reputationDemandLabel(1.0)).toBe('Baseline passenger demand for your reputation.')
  })
})
