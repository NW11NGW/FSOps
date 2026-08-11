import { describe, expect, it } from 'vitest'

import {
  blockVarianceMinutes,
  isRecentlyLanded,
  netEarned,
  progressPercent,
  projectEtaIso,
  remainingBlockMinutes,
  selectMaintenanceWarning,
  selectNextFlights,
} from './panelFormat'
import type { Flight, FlightOption } from '@/types/flight'
import type { FleetAircraftSummary } from '@/types/fleet'

function aircraft(overrides: Partial<FleetAircraftSummary> & { id: string }): FleetAircraftSummary {
  return {
    registration: 'G-TEST',
    aircraftTypeId: 'type-1',
    aircraftTypeName: 'A320',
    family: 'A320',
    paxCapacity: 180,
    ownership: 'Owned',
    status: 'Active',
    locationIcao: 'EGLL',
    airframeHours: 1000,
    hoursSinceACheck: 50,
    hoursSinceCCheck: 500,
    hoursToNextACheck: 450,
    hoursToNextCCheck: 3500,
    conditionPercent: 90,
    fuelOnBoardKg: 5000,
    groundedUntilUtc: null,
    groundedReason: null,
    reservedForPlayer: false,
    createdUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function flightOption(overrides: Partial<FlightOption> & { routeId: string }): FlightOption {
  return {
    flightNumber: '100',
    departureIcao: 'EGLL',
    departureName: 'Heathrow',
    arrivalIcao: 'LFPG',
    arrivalName: 'Charles de Gaulle',
    distanceNm: 200,
    estimatedBlockMinutes: 60,
    isFlyable: true,
    reason: null,
    aircraftOptions: [],
    ...overrides,
  }
}

function flight(overrides: Partial<Flight> = {}): Flight {
  return {
    id: 'flight-1',
    airlineId: 'airline-1',
    routeId: 'route-1',
    fleetAircraftId: 'ac-1',
    pilotId: 'pilot-1',
    status: 'Completed',
    plannedDepartureUtc: '2026-08-11T10:00:00Z',
    plannedBlockMinutes: 90,
    outUtc: '2026-08-11T10:05:00Z',
    offUtc: '2026-08-11T10:15:00Z',
    onUtc: '2026-08-11T11:30:00Z',
    inUtc: '2026-08-11T11:35:00Z',
    paxBooked: 150,
    paxFlown: 150,
    fuelPlannedKg: 4000,
    fuelUsedKg: 3900,
    landingFpmFirst: -180,
    landingFpmHardest: -180,
    landingGForce: 1.2,
    centrelineDeviationM: 3,
    titleFlown: 'A320neo',
    typeMismatch: false,
    simRateElevated: false,
    maxSimulationRateObserved: 1,
    slewDetected: false,
    positionJumpDetected: false,
    revenue: 5000,
    totalCost: 1800,
    createdUtc: '2026-08-11T10:00:00Z',
    ...overrides,
  }
}

describe('remainingBlockMinutes', () => {
  it('subtracts elapsed from planned', () => {
    expect(remainingBlockMinutes(30, 90)).toBe(60)
  })

  it('never goes negative once elapsed overruns planned', () => {
    expect(remainingBlockMinutes(100, 90)).toBe(0)
  })
})

describe('progressPercent', () => {
  it('computes a fraction of planned block time', () => {
    expect(progressPercent(45, 90)).toBe(50)
  })

  it('clamps at 100 once elapsed overruns planned', () => {
    expect(progressPercent(120, 90)).toBe(100)
  })

  it('returns 0 rather than dividing by zero when planned is not yet known', () => {
    expect(progressPercent(10, 0)).toBe(0)
  })
})

describe('projectEtaIso', () => {
  it('adds minutes onto the given instant', () => {
    expect(projectEtaIso('2026-08-11T10:00:00.000Z', 30)).toBe('2026-08-11T10:30:00.000Z')
  })

  it('falls back to the input when it cannot be parsed', () => {
    expect(projectEtaIso('not-a-date', 30)).toBe('not-a-date')
  })
})

describe('blockVarianceMinutes', () => {
  it('is positive when the flight ran long', () => {
    expect(blockVarianceMinutes(100, 90)).toBe(10)
  })

  it('is negative when the flight finished early', () => {
    expect(blockVarianceMinutes(80, 90)).toBe(-10)
  })
})

describe('netEarned', () => {
  it('subtracts total cost from revenue', () => {
    expect(netEarned({ revenue: 5000, totalCost: 1800 })).toBe(3200)
  })

  it('can be negative when costs exceeded revenue', () => {
    expect(netEarned({ revenue: 500, totalCost: 1800 })).toBe(-1300)
  })
})

describe('isRecentlyLanded', () => {
  it('is true for a completed flight that landed a few minutes ago', () => {
    const f = flight({ status: 'Completed', inUtc: '2026-08-11T11:50:00Z' })
    expect(isRecentlyLanded(f, '2026-08-11T12:00:00Z', 15)).toBe(true)
  })

  it('is false once the window has passed', () => {
    const f = flight({ status: 'Completed', inUtc: '2026-08-11T11:00:00Z' })
    expect(isRecentlyLanded(f, '2026-08-11T12:00:00Z', 15)).toBe(false)
  })

  it('is false for a flight that never landed', () => {
    const f = flight({ status: 'Abandoned', inUtc: null })
    expect(isRecentlyLanded(f, '2026-08-11T12:00:00Z', 15)).toBe(false)
  })

  it('is false for a flight still in progress', () => {
    const f = flight({ status: 'InProgress', inUtc: null })
    expect(isRecentlyLanded(f, '2026-08-11T12:00:00Z', 15)).toBe(false)
  })
})

describe('selectMaintenanceWarning', () => {
  it('returns null when nothing in the fleet is close to a check', () => {
    const fleet = [aircraft({ id: 'a1' }), aircraft({ id: 'a2', hoursToNextACheck: 300 })]
    expect(selectMaintenanceWarning(fleet)).toBeNull()
  })

  it('flags an aircraft nearing its A-check', () => {
    const fleet = [aircraft({ id: 'a1', registration: 'G-CLOSE', hoursToNextACheck: 12 })]
    const warning = selectMaintenanceWarning(fleet)
    expect(warning?.aircraft.registration).toBe('G-CLOSE')
    expect(warning?.checkType).toBe('A')
    expect(warning?.hoursRemaining).toBe(12)
  })

  it('flags an aircraft nearing its C-check', () => {
    const fleet = [aircraft({ id: 'a1', registration: 'G-CCHECK', hoursToNextCCheck: 80 })]
    const warning = selectMaintenanceWarning(fleet)
    expect(warning?.checkType).toBe('C')
  })

  it('picks the single most urgent aircraft across the fleet', () => {
    const fleet = [
      aircraft({ id: 'a1', registration: 'G-SOON', hoursToNextACheck: 15 }),
      aircraft({ id: 'a2', registration: 'G-URGENT', hoursToNextACheck: 3 }),
    ]
    const warning = selectMaintenanceWarning(fleet)
    expect(warning?.aircraft.registration).toBe('G-URGENT')
  })

  it('ignores an aircraft already in maintenance', () => {
    const fleet = [aircraft({ id: 'a1', status: 'InMaintenance', hoursToNextACheck: 1 })]
    expect(selectMaintenanceWarning(fleet)).toBeNull()
  })
})

describe('selectNextFlights', () => {
  it('filters out routes that are not currently flyable', () => {
    const options = [
      flightOption({ routeId: 'r1', isFlyable: true }),
      flightOption({ routeId: 'r2', isFlyable: false }),
    ]
    expect(selectNextFlights(options).map((o) => o.routeId)).toEqual(['r1'])
  })

  it('caps the result at the given limit', () => {
    const options = [
      flightOption({ routeId: 'r1' }),
      flightOption({ routeId: 'r2' }),
      flightOption({ routeId: 'r3' }),
      flightOption({ routeId: 'r4' }),
    ]
    expect(selectNextFlights(options, 2)).toHaveLength(2)
  })
})
