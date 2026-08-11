import { describe, expect, it } from 'vitest'

import {
  addLegToDay,
  clearDay,
  draftLegFromOption,
  draftWeekToInput,
  findOverlappingLeg,
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
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100' }
    const result = draftLegFromOption(option, '08:15', 90)
    expect(result.departureTimeUtc).toBe('08:15:00')
    expect(result.isNew).toBe(true)
    expect(result.blockMinutes).toBe(90)
  })

  it('leaves an already-full "HH:mm:ss" time untouched', () => {
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100' }
    const result = draftLegFromOption(option, '08:15:30', 90)
    expect(result.departureTimeUtc).toBe('08:15:30')
  })

  it('generates a unique id on every call', () => {
    const option: LegalLegOption = { routeId: 'route-1', departureIcao: 'EGLL', arrivalIcao: 'LFPG', flightNumber: 'FS100' }
    const a = draftLegFromOption(option, '08:15', 90)
    const b = draftLegFromOption(option, '08:15', 90)
    expect(a.id).not.toBe(b.id)
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
