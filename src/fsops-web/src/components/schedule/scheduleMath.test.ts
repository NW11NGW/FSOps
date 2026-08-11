import { describe, expect, it } from 'vitest'

import { layoutDay, minuteToHHMM, pixelsToSnappedMinute } from './scheduleMath'
import { MIN_BLOCK_PX, PX_PER_MIN } from './types'
import type { DraftLeg, DraftWeek } from './draftEntry'

function leg(overrides: Partial<DraftLeg> & { id: string; departureTimeUtc: string; blockMinutes: number }): DraftLeg {
  return {
    routeId: 'route-1',
    departureIcao: 'EGLL',
    arrivalIcao: 'LFPG',
    flightNumber: 'FS100',
    isNew: false,
    ...overrides,
  }
}

describe('layoutDay', () => {
  it('positions a block at start*PX_PER_MIN with height clamped to MIN_BLOCK_PX', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 10 })],
      },
    }
    const result = layoutDay(1, week)
    expect(result.blocks).toHaveLength(1)
    expect(result.blocks[0]?.top).toBe(480 * PX_PER_MIN)
    // A 10-minute leg is shorter than the floor - height must not shrink below MIN_BLOCK_PX.
    expect(result.blocks[0]?.height).toBe(MIN_BLOCK_PX)
  })

  it('gives a long leg its real height rather than the floor', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 120 })],
      },
    }
    const result = layoutDay(1, week)
    expect(result.blocks[0]?.height).toBe(120 * PX_PER_MIN)
  })

  it('flags two overlapping legs on the same day as overlapping, and a clear day as not', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 120 }),
          leg({ id: 'l2', departureTimeUtc: '09:00:00', blockMinutes: 60 }),
        ],
      },
    }
    const result = layoutDay(1, week)
    expect(result.blocks.every((b) => b.overlapping)).toBe(true)
  })

  it('does not flag two legs with a gap between them as overlapping', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 }),
          leg({ id: 'l2', departureTimeUtc: '10:00:00', blockMinutes: 60 }),
        ],
      },
    }
    const result = layoutDay(1, week)
    expect(result.blocks.every((b) => !b.overlapping)).toBe(true)
  })

  it('reports overflowMinutes for a leg that departs before midnight and lands after it', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'l1', departureTimeUtc: '23:30:00', blockMinutes: 90 })],
      },
    }
    const result = layoutDay(1, week)
    // Departs 23:30 (1410), block 90 -> ends at 1500, 60 minutes past the 1440-minute day.
    expect(result.blocks[0]?.overflowMinutes).toBe(60)
  })

  it('produces a spillover block on the NEXT day for a leg that crosses midnight', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'l1', departureTimeUtc: '23:30:00', blockMinutes: 90 })],
      },
    }
    // Tuesday (day 2) is the day after Monday (day 1) - it should show the spillover strip.
    const tuesday = layoutDay(2, week)
    expect(tuesday.spillovers).toHaveLength(1)
    expect(tuesday.spillovers[0]?.entry.id).toBe('l1')
  })

  it('computes a turnaround gap between two consecutive legs and flags it tight below the threshold', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 }), // ends 09:00
          leg({ id: 'l2', departureTimeUtc: '09:20:00', blockMinutes: 60 }), // 20-minute gap
        ],
      },
    }
    const result = layoutDay(1, week)
    expect(result.gaps).toHaveLength(1)
    expect(result.gaps[0]?.minutes).toBe(20)
    expect(result.gaps[0]?.tight).toBe(true) // below TIGHT_TURNAROUND_MINUTES (45)
  })

  it('does not flag a comfortable turnaround as tight', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 }), // ends 09:00
          leg({ id: 'l2', departureTimeUtc: '10:00:00', blockMinutes: 60 }), // 60-minute gap
        ],
      },
    }
    const result = layoutDay(1, week)
    expect(result.gaps[0]?.tight).toBe(false)
  })

  it('returns an empty layout for a day with no aircraft assigned', () => {
    const result = layoutDay(3, {})
    expect(result.fleetAircraftId).toBeNull()
    expect(result.blocks).toHaveLength(0)
    expect(result.spillovers).toHaveLength(0)
    expect(result.gaps).toHaveLength(0)
  })
})

describe('pixelsToSnappedMinute', () => {
  it('snaps to the nearest 5-minute mark', () => {
    expect(pixelsToSnappedMinute(482)).toBe(480)
    expect(pixelsToSnappedMinute(483)).toBe(485)
  })

  it('clamps below zero up to zero', () => {
    expect(pixelsToSnappedMinute(-50)).toBe(0)
  })

  it('clamps above the last valid mark of the day', () => {
    expect(pixelsToSnappedMinute(100000)).toBe(1435)
  })
})

describe('minuteToHHMM', () => {
  it('formats minutes-of-day as zero-padded HH:MM', () => {
    expect(minuteToHHMM(0)).toBe('00:00')
    expect(minuteToHHMM(65)).toBe('01:05')
    expect(minuteToHHMM(1439)).toBe('23:59')
  })
})
