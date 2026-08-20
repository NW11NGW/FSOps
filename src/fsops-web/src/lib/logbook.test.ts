import { describe, expect, it } from 'vitest'

import { blockDeltaMinutes, DEFAULT_LOGBOOK_FILTERS, filterSectors, sortSectors, totalsFor } from './logbook'
import type { LogbookSector } from '@/types/flight'

function sector(overrides: Partial<LogbookSector> = {}): LogbookSector {
  return {
    flightId: 'f1',
    status: 'Completed',
    routeId: 'r1',
    contract: null,
    departureIcao: 'EGGD',
    arrivalIcao: 'EGPH',
    flightNumber: '101',
    registration: 'G-TEST',
    aircraftTypeName: 'A320neo',
    aircraftIcaoType: 'A320',
    pilotName: 'Robin Hayes',
    isPlayerFlight: false,
    dateUtc: '2026-08-10T12:00:00Z',
    outUtc: '2026-08-10T10:30:00Z',
    inUtc: '2026-08-10T12:00:00Z',
    plannedBlockMinutes: 90,
    actualBlockMinutes: 90,
    blockTimeNotMeasured: false,
    paxFlown: 120,
    paxBooked: 130,
    seats: 180,
    loadFactorPercent: 66.7,
    landingFpmFirst: -150,
    fuelUsedKg: 2100,
    revenue: 18000,
    cost: 12000,
    net: 6000,
    simRateElevated: false,
    slewDetected: false,
    positionJumpDetected: false,
    vatsimOnline: null,
    hasTrack: true,
    trackPointCount: 360,
    ...overrides,
  }
}

describe('blockDeltaMinutes', () => {
  it('is the signed difference from plan', () => {
    expect(blockDeltaMinutes(sector({ plannedBlockMinutes: 90, actualBlockMinutes: 102 }))).toBe(12)
    expect(blockDeltaMinutes(sector({ plannedBlockMinutes: 90, actualBlockMinutes: 84 }))).toBe(-6)
  })

  it('is null when block time was never measurable', () => {
    expect(blockDeltaMinutes(sector({ actualBlockMinutes: null }))).toBeNull()
  })
})

describe('filterSectors', () => {
  const rows = [
    sector({ flightId: 'a', departureIcao: 'EGGD', arrivalIcao: 'EGPH', registration: 'G-ABCD', pilotName: 'Robin Hayes', isPlayerFlight: false, status: 'Completed', hasTrack: false }),
    sector({ flightId: 'b', departureIcao: 'EGPH', arrivalIcao: 'EGSS', registration: 'G-WXYZ', pilotName: 'You', isPlayerFlight: true, status: 'Abandoned', hasTrack: true }),
  ]

  it('returns everything by default', () => {
    expect(filterSectors(rows, DEFAULT_LOGBOOK_FILTERS)).toHaveLength(2)
  })

  it('matches free text against airports, registration and pilot', () => {
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, query: 'egss' }).map((s) => s.flightId)).toEqual(['b'])
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, query: 'G-ABCD' }).map((s) => s.flightId)).toEqual(['a'])
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, query: 'robin' }).map((s) => s.flightId)).toEqual(['a'])
  })

  it('matches a city pair typed as one string', () => {
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, query: 'EGGD-EGPH' }).map((s) => s.flightId)).toEqual(['a'])
  })

  it('filters by status, by who was flying, and by having a track', () => {
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, status: 'Abandoned' }).map((s) => s.flightId)).toEqual(['b'])
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, flownBy: 'mine' }).map((s) => s.flightId)).toEqual(['b'])
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, flownBy: 'crew' }).map((s) => s.flightId)).toEqual(['a'])
    expect(filterSectors(rows, { ...DEFAULT_LOGBOOK_FILTERS, withTrackOnly: true }).map((s) => s.flightId)).toEqual(['b'])
  })
})

describe('sortSectors', () => {
  it('sorts by net in both directions', () => {
    const rows = [sector({ flightId: 'a', net: 100 }), sector({ flightId: 'b', net: -50 }), sector({ flightId: 'c', net: 900 })]
    expect(sortSectors(rows, 'net', 'desc').map((s) => s.flightId)).toEqual(['c', 'a', 'b'])
    expect(sortSectors(rows, 'net', 'asc').map((s) => s.flightId)).toEqual(['b', 'a', 'c'])
  })

  it('puts a not-measured value last in BOTH directions, never first', () => {
    // This is the whole point. A landing whose rate the sim never reported is not "the smoothest
    // landing"; letting it float to the top of an ascending sort would present "we could not
    // measure this" as a measurement, which is exactly what this feature exists to prevent.
    const rows = [
      sector({ flightId: 'measured-hard', landingFpmFirst: -420 }),
      sector({ flightId: 'unmeasured', landingFpmFirst: null }),
      sector({ flightId: 'measured-smooth', landingFpmFirst: -110 }),
    ]

    expect(sortSectors(rows, 'landing', 'asc').map((s) => s.flightId)).toEqual(['measured-smooth', 'measured-hard', 'unmeasured'])
    expect(sortSectors(rows, 'landing', 'desc').map((s) => s.flightId)).toEqual(['measured-hard', 'measured-smooth', 'unmeasured'])
  })

  it('sorts landings by sink-rate magnitude, so ascending is smoothest first', () => {
    const rows = [sector({ flightId: 'hard', landingFpmFirst: -600 }), sector({ flightId: 'smooth', landingFpmFirst: 90 })]
    expect(sortSectors(rows, 'landing', 'asc').map((s) => s.flightId)).toEqual(['smooth', 'hard'])
  })

  it('sorts a not-measured block delta last too', () => {
    const rows = [
      sector({ flightId: 'late', plannedBlockMinutes: 90, actualBlockMinutes: 120 }),
      sector({ flightId: 'unmeasured', actualBlockMinutes: null, blockTimeNotMeasured: true }),
      sector({ flightId: 'early', plannedBlockMinutes: 90, actualBlockMinutes: 80 }),
    ]
    expect(sortSectors(rows, 'blockDelta', 'asc').map((s) => s.flightId)).toEqual(['early', 'late', 'unmeasured'])
    expect(sortSectors(rows, 'blockDelta', 'desc').map((s) => s.flightId)).toEqual(['late', 'early', 'unmeasured'])
  })

  it('does not mutate the array it was given', () => {
    const rows = [sector({ flightId: 'a', net: 1 }), sector({ flightId: 'b', net: 2 })]
    sortSectors(rows, 'net', 'desc')
    expect(rows.map((s) => s.flightId)).toEqual(['a', 'b'])
  })
})

describe('totalsFor', () => {
  it('sums net and block time over exactly the rows given', () => {
    const totals = totalsFor([sector({ net: 1000, actualBlockMinutes: 90 }), sector({ net: -400, actualBlockMinutes: 60 })])
    expect(totals.sectors).toBe(2)
    expect(totals.net).toBe(600)
    expect(totals.blockMinutes).toBe(150)
    expect(totals.unmeasuredBlockSectors).toBe(0)
  })

  it('counts sectors whose block time could not be measured, rather than treating them as zero-length', () => {
    // The hours figure is then a floor, and the page says so - silently under-reporting would be
    // the worse of the two failures.
    const totals = totalsFor([sector({ actualBlockMinutes: 90 }), sector({ actualBlockMinutes: null })])
    expect(totals.blockMinutes).toBe(90)
    expect(totals.unmeasuredBlockSectors).toBe(1)
  })
})
