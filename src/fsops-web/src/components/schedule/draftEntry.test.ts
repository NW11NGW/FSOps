import { describe, expect, it } from 'vitest'

import {
  addLegToDay,
  clearDay,
  draftLegFromOption,
  draftWeekToInput,
  findLegsOrphanedByRemoval,
  findOverlappingLeg,
  removeLegAndOrphans,
  removeLegFromDay,
  scheduleToDraftWeek,
  setDayAircraft,
  updateLegTime,
  weekSignature,
  type DraftLeg,
  type DraftWeek,
} from './draftEntry'
import type { LegalLegOption, ScheduleDutyDay } from '@/types/schedule'

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

describe('scheduleToDraftWeek', () => {
  it('carries over saved duty days keyed by day-of-week', () => {
    const dutyDays: ScheduleDutyDay[] = [
      {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          {
            id: 'l1',
            departureTimeUtc: '08:00:00',
            routeId: 'route-1',
            departureIcao: 'EGLL',
            arrivalIcao: 'LFPG',
            flightNumber: 'FS100',
            blockMinutes: 90,
          },
        ],
      },
    ]
    const week = scheduleToDraftWeek(dutyDays)
    expect(week[1]?.fleetAircraftId).toBe('ac-1')
    expect(week[1]?.legs[0]?.isNew).toBe(false)
    expect(week[2]).toBeUndefined()
  })

  it('defaults a missing registration to an em dash and a null blockMinutes to 60', () => {
    const dutyDays: ScheduleDutyDay[] = [
      {
        dayOfWeek: 0,
        fleetAircraftId: 'ac-2',
        registration: null,
        legs: [
          { id: 'l2', departureTimeUtc: '10:00:00', routeId: 'route-2', departureIcao: 'KJFK', arrivalIcao: 'KLAX', flightNumber: null, blockMinutes: null },
        ],
      },
    ]
    const week = scheduleToDraftWeek(dutyDays)
    expect(week[0]?.registration).toBe('—')
    expect(week[0]?.legs[0]?.blockMinutes).toBe(60)
  })
})

describe('draftWeekToInput', () => {
  it('omits days with an aircraft chosen but no legs yet', () => {
    const week: DraftWeek = {
      1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [] },
      2: { dayOfWeek: 2, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] },
    }
    const input = draftWeekToInput(week)
    expect(input).toHaveLength(1)
    expect(input[0]?.dayOfWeek).toBe(2)
  })

  it('sends only departureTimeUtc and routeId per leg, not the client-side fields', () => {
    const week: DraftWeek = {
      1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] },
    }
    const input = draftWeekToInput(week)
    expect(input[0]?.legs[0]).toEqual({ departureTimeUtc: '08:00:00', routeId: 'route-1' })
  })
})

describe('weekSignature', () => {
  it('is identical for the same week regardless of leg insertion order', () => {
    const legA = leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })
    const legB = leg({ id: 'l2', departureTimeUtc: '10:00:00', blockMinutes: 60, routeId: 'route-2' })
    const weekAB: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [legA, legB] } }
    const weekBA: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [legB, legA] } }
    expect(weekSignature(weekAB)).toBe(weekSignature(weekBA))
  })

  it('differs when a leg time changes', () => {
    const week1: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] } }
    const week2: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '09:00:00', blockMinutes: 60 })] } }
    expect(weekSignature(week1)).not.toBe(weekSignature(week2))
  })
})

describe('setDayAircraft', () => {
  it('clears existing legs when the aircraft actually changes', () => {
    const week: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] } }
    const next = setDayAircraft(week, 1, 'ac-2', 'G-WXYZ')
    expect(next[1]?.legs).toHaveLength(0)
    expect(next[1]?.fleetAircraftId).toBe('ac-2')
  })

  it('keeps existing legs when re-setting the SAME aircraft', () => {
    const week: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] } }
    const next = setDayAircraft(week, 1, 'ac-1', 'G-ABCD')
    expect(next[1]?.legs).toHaveLength(1)
  })

  it('does not mutate the original week object', () => {
    const week: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [] } }
    setDayAircraft(week, 1, 'ac-2', 'G-WXYZ')
    expect(week[1]?.fleetAircraftId).toBe('ac-1')
  })
})

describe('clearDay / addLegToDay / removeLegFromDay / updateLegTime', () => {
  it('clearDay removes the day entirely, not just its legs', () => {
    const week: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [] } }
    const next = clearDay(week, 1)
    expect(next[1]).toBeUndefined()
  })

  it('addLegToDay is a no-op when the day has no aircraft chosen', () => {
    const week: DraftWeek = {}
    const next = addLegToDay(week, 1, leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 }))
    expect(next).toEqual({})
  })

  it('removeLegFromDay removes only the targeted leg', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 }), leg({ id: 'l2', departureTimeUtc: '10:00:00', blockMinutes: 60 })],
      },
    }
    const next = removeLegFromDay(week, 1, 'l1')
    expect(next[1]?.legs.map((l) => l.id)).toEqual(['l2'])
  })

  it('updateLegTime updates only the matching leg', () => {
    const week: DraftWeek = {
      1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 60 })] },
    }
    const next = updateLegTime(week, 1, 'l1', '09:30:00')
    expect(next[1]?.legs[0]?.departureTimeUtc).toBe('09:30:00')
  })
})

describe('draftLegFromOption', () => {
  it('pads an "HH:mm" time to "HH:mm:ss"', () => {
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100', blockMinutes: 90, warnings: [] }
    const result = draftLegFromOption(option, '08:15', 90)
    expect(result.departureTimeUtc).toBe('08:15:00')
    expect(result.isNew).toBe(true)
    expect(result.blockMinutes).toBe(90)
  })

  it('leaves an already-full "HH:mm:ss" time untouched', () => {
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100', blockMinutes: 90, warnings: [] }
    const result = draftLegFromOption(option, '08:15:30', 90)
    expect(result.departureTimeUtc).toBe('08:15:30')
  })

  it('generates a unique id on every call', () => {
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100', blockMinutes: 90, warnings: [] }
    const a = draftLegFromOption(option, '08:15', 90)
    const b = draftLegFromOption(option, '08:15', 90)
    expect(a.id).not.toBe(b.id)
  })
})

describe('findLegsOrphanedByRemoval', () => {
  it('returns [] when the day or the leg being removed does not exist', () => {
    expect(findLegsOrphanedByRemoval({}, 1, 'nope')).toEqual([])
    const week: DraftWeek = { 1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-ABCD', legs: [] } }
    expect(findLegsOrphanedByRemoval(week, 1, 'nope')).toEqual([])
  })

  it('orphans the return half of a simple out-and-back when the outbound is removed', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const orphaned = findLegsOrphanedByRemoval(week, 1, 'out', 'EGLL')
    expect(orphaned).toEqual([{ day: 1, leg: week[1]!.legs[1], aircraftActuallyAt: 'EGLL' }])
  })

  it('orphans a leg SEVERAL positions later, not just the one immediately after the gap', () => {
    // A: EGLL -> EGKK, B: EGKK -> EGPH (removed), C: EGPH -> EGCC, D: EGCC -> EGLL.
    // C's own departure (EGPH) still matches what would have been B's arrival on paper, and D's
    // departure (EGCC) still matches C's own arrival - so a check that only looked at the pair
    // adjacent to the removal would flag nothing beyond C. The aircraft is really still stuck at
    // EGKK (A's arrival) once B is gone, so BOTH C and D are unflyable as scheduled.
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'a', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'b', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGPH' }),
          leg({ id: 'c', departureTimeUtc: '12:00:00', blockMinutes: 60, departureIcao: 'EGPH', arrivalIcao: 'EGCC' }),
          leg({ id: 'd', departureTimeUtc: '14:00:00', blockMinutes: 60, departureIcao: 'EGCC', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const orphaned = findLegsOrphanedByRemoval(week, 1, 'b', 'EGLL')
    expect(orphaned.map((o) => o.leg.id)).toEqual(['c', 'd'])
    expect(orphaned.every((o) => o.aircraftActuallyAt === 'EGKK')).toBe(true)
  })

  it('reports nothing when the removed leg has no successor depending on it', () => {
    // Two independent out-and-backs on the same aircraft, different days. Removing the SECOND
    // rotation's return leg leaves nothing later in the week for this aircraft to strand.
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'a', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'b', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
      2: {
        dayOfWeek: 2,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'c', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGPH' }),
          leg({ id: 'd', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGPH', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    expect(findLegsOrphanedByRemoval(week, 2, 'd', 'EGLL')).toEqual([])
  })

  it('reports nothing when the aircraft is already recorded at the remaining leg\'s departure', () => {
    // Same shape as the simple out-and-back case above, but this time the aircraft's real recorded
    // location IS the away airport (e.g. it genuinely ended an earlier week there) - the return is
    // actually flyable as drafted, so nothing should be reported.
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    expect(findLegsOrphanedByRemoval(week, 1, 'out', 'EGKK')).toEqual([])
  })

  it('trusts the first remaining entry when the aircraft location is unknown, but still catches breaks further in', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'a', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGPH' }),
          leg({ id: 'gap', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGPH', arrivalIcao: 'EGCC' }),
          leg({ id: 'broken', departureTimeUtc: '12:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
        ],
      },
    }
    // Removing 'gap' leaves 'a' (EGKK -> EGPH, never anchored against any known real location - not
    // flagged) followed by 'broken' (departs EGLL, but 'a' really only gets the aircraft to EGPH).
    const orphaned = findLegsOrphanedByRemoval(week, 1, 'gap')
    expect(orphaned.map((o) => o.leg.id)).toEqual(['broken'])
    expect(orphaned[0]?.aircraftActuallyAt).toBe('EGPH')
  })

  it('never considers legs flown by a different aircraft', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' })],
      },
      2: {
        dayOfWeek: 2,
        fleetAircraftId: 'ac-2',
        registration: 'G-WXYZ',
        legs: [leg({ id: 'other', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL', routeId: 'route-2' })],
      },
    }
    expect(findLegsOrphanedByRemoval(week, 1, 'out', 'EGLL')).toEqual([])
  })

  it('does not mutate the input week', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const snapshot = JSON.parse(JSON.stringify(week))
    findLegsOrphanedByRemoval(week, 1, 'out', 'EGLL')
    expect(week).toEqual(snapshot)
  })
})

describe('removeLegAndOrphans', () => {
  it('removes only the requested leg when nothing is orphaned', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const next = removeLegAndOrphans(week, 1, 'ret', [])
    expect(next[1]?.legs.map((l) => l.id)).toEqual(['out'])
  })

  it('removes the requested leg AND every orphan it was handed, in one step', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const orphans = findLegsOrphanedByRemoval(week, 1, 'out', 'EGLL')
    const next = removeLegAndOrphans(week, 1, 'out', orphans)
    // Both legs gone, and since the day now has none left, its aircraft is released too - the whole
    // day disappears from the draft rather than sitting around with an aircraft and nothing on it.
    expect(next[1]).toBeUndefined()
  })

  it('only clears a day whose own legs all got removed, not every day the aircraft touches', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'a', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'b', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGPH' }),
        ],
      },
      2: {
        dayOfWeek: 2,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'c', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGPH', arrivalIcao: 'EGCC' }),
          leg({ id: 'd', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGCC', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const orphans = findLegsOrphanedByRemoval(week, 1, 'b', 'EGLL')
    expect(orphans.map((o) => o.leg.id)).toEqual(['c', 'd'])
    const next = removeLegAndOrphans(week, 1, 'b', orphans)
    // Day 1 loses 'b' but keeps 'a' - not cleared. Day 2 loses both its legs - fully cleared.
    expect(next[1]?.legs.map((l) => l.id)).toEqual(['a'])
    expect(next[2]).toBeUndefined()
  })

  it('does not mutate the input week', () => {
    const week: DraftWeek = {
      1: {
        dayOfWeek: 1,
        fleetAircraftId: 'ac-1',
        registration: 'G-ABCD',
        legs: [
          leg({ id: 'out', departureTimeUtc: '08:00:00', blockMinutes: 60, departureIcao: 'EGLL', arrivalIcao: 'EGKK' }),
          leg({ id: 'ret', departureTimeUtc: '10:00:00', blockMinutes: 60, departureIcao: 'EGKK', arrivalIcao: 'EGLL' }),
        ],
      },
    }
    const snapshot = JSON.parse(JSON.stringify(week))
    const orphans = findLegsOrphanedByRemoval(week, 1, 'out', 'EGLL')
    removeLegAndOrphans(week, 1, 'out', orphans)
    expect(week).toEqual(snapshot)
  })
})

describe('findOverlappingLeg', () => {
  const existing = [leg({ id: 'l1', departureTimeUtc: '08:00:00', blockMinutes: 120 })]

  it('finds a leg whose time range overlaps the candidate', () => {
    const found = findOverlappingLeg({ departureTimeUtc: '09:00:00', blockMinutes: 30 }, existing)
    expect(found?.id).toBe('l1')
  })

  it('does not treat back-to-back legs (end === next start) as overlapping', () => {
    // l1 runs 08:00-10:00; a candidate starting exactly at 10:00 is a legal turnaround, not a clash.
    const found = findOverlappingLeg({ departureTimeUtc: '10:00:00', blockMinutes: 30 }, existing)
    expect(found).toBeNull()
  })

  it('excludes the leg being edited from the overlap check', () => {
    const found = findOverlappingLeg({ departureTimeUtc: '08:30:00', blockMinutes: 30 }, existing, 'l1')
    expect(found).toBeNull()
  })

  it('returns null when nothing overlaps', () => {
    const found = findOverlappingLeg({ departureTimeUtc: '12:00:00', blockMinutes: 30 }, existing)
    expect(found).toBeNull()
  })
})
