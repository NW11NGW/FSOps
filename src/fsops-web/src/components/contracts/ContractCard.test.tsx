import { describe, expect, it, vi } from 'vitest'

import { ContractCard } from './ContractCard'
import { contractScale, isExpedition } from './contractKind'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { Contract, ContractLeg } from '@/types/contract'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function stubSettings() {
  vi.mocked(get).mockImplementation(async (path: string) => {
    const settings = settingsResponseFor(path)
    if (settings) return settings as never
    throw new Error(`Unexpected GET ${path}`)
  })
}

function leg(sequence: number, departureIcao: string, arrivalIcao: string, overrides: Partial<ContractLeg> = {}): ContractLeg {
  return {
    id: `leg-${sequence}`,
    sequence,
    departureIcao,
    arrivalIcao,
    distanceNm: 300,
    plannedBlockMinutes: 150,
    feeShare: 2000,
    // Null rather than 0: the leg has not flown, which is not the same as having paid nothing.
    feePaid: null,
    flown: false,
    flightId: null,
    flownUtc: null,
    ...overrides,
  }
}

function contract(overrides: Partial<Contract> = {}): Contract {
  return {
    id: 'contract-1',
    kind: 'Cargo',
    status: 'Offered',
    operatorName: 'Northgate Freight',
    aircraft: {
      typeDesignator: 'C208',
      name: 'Cessna 208B Grand Caravan',
      manufacturer: 'Cessna',
      category: 'UtilityTurboprop',
      rangeNm: 900,
      cruiseTasKts: 175,
      seats: 0,
    },
    loadDescription: '1,240 kg of machine parts',
    payloadKg: 1240,
    paxCount: 0,
    fee: 4000,
    // Single-leg default: no bonus, which is the rule rather than a fixture convenience.
    completionBonus: 0,
    totalIfCompleted: 4000,
    totalDistanceNm: 300,
    totalPlannedBlockMinutes: 150,
    legCount: 1,
    flownLegCount: 0,
    earnedSoFar: 0,
    outstandingFee: 4000,
    abandonCharge: 0,
    abandonReason: 'No leg was flown, so the aircraft is still where its operator left it - handing the job back costs nothing.',
    offeredUtc: '2026-08-20T00:00:00Z',
    deadlineUtc: '2026-09-17T00:00:00Z',
    acceptedUtc: null,
    closedUtc: null,
    closedReason: null,
    nextLeg: { id: 'leg-1', sequence: 1, departureIcao: 'EGGD', arrivalIcao: 'EGPH' },
    legs: [leg(1, 'EGGD', 'EGPH')],
    ...overrides,
  }
}

const ferry = contract({
  id: 'contract-ferry',
  kind: 'Ferry',
  operatorName: 'Meridian Aviation',
  loadDescription: 'Positioning flight - Cessna 172 Skyhawk, empty',
  payloadKg: 0,
  fee: 22000,
  completionBonus: 6000,
  totalIfCompleted: 28000,
  totalDistanceNm: 3100,
  totalPlannedBlockMinutes: 1560,
  legCount: 6,
  outstandingFee: 22000,
  legs: [
    leg(1, 'EGGD', 'EGPC'),
    leg(2, 'EGPC', 'BIRK'),
    leg(3, 'BIRK', 'BGBW'),
    leg(4, 'BGBW', 'BGGH'),
    leg(5, 'BGGH', 'CYFB'),
    leg(6, 'CYFB', 'CYYZ'),
  ],
  nextLeg: { id: 'leg-1', sequence: 1, departureIcao: 'EGGD', arrivalIcao: 'EGPC' },
})

describe('ContractCard - the three kinds have to read as different work', () => {
  it('a cargo job leads with what is in the hold', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={contract()} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('Cargo')
    expect(body).toContain('In the hold')
    expect(body).toContain('1,240 kg of machine parts')
    // One sector: the route line already is the chain, so no chain block is drawn.
    expect(body).not.toContain('flown in order')

    mounted.unmount()
  })

  it('a charter leads with who is on board', async () => {
    stubSettings()
    const charter = contract({
      kind: 'Charter',
      loadDescription: '6 passengers',
      payloadKg: 0,
      paxCount: 6,
    })
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={charter} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('Charter')
    expect(body).toContain('On board')
    expect(body).toContain('6 passengers')

    mounted.unmount()
  })

  /**
   * The headline requirement: a multi-leg ferry has to read as an expedition rather than as another
   * row. It names every stop in order, says how many legs there are, and shows the endpoints of the
   * whole journey rather than of its first sector.
   */
  it('a multi-leg ferry shows its whole chain of stops, in order, and its true endpoints', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={ferry} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)

    expect(body).toContain('Ferry')
    expect(body).toContain('The chain — 6 legs, flown in order')

    // Every intermediate stop is named, not just the endpoints.
    for (const icao of ['EGPC', 'BIRK', 'BGBW', 'BGGH', 'CYFB']) {
      expect(body).toContain(icao)
    }

    // The route headline spans the WHOLE job - first departure to last arrival - rather than showing
    // the first leg and quietly implying that is the trip.
    expect(body).toContain('EGGD → CYYZ')

    // And it is marked as the commitment it is.
    expect(body).toContain('Multi-day')

    mounted.unmount()
  })

  it('an accepted job shows what has been earned and what is still to come', async () => {
    stubSettings()
    const partway = contract({
      ...ferry,
      status: 'Accepted',
      flownLegCount: 2,
      earnedSoFar: 7000,
      outstandingFee: 15000,
      legs: ferry.legs!.map((l) =>
        l.sequence <= 2 ? { ...l, flown: true, flightId: `f-${l.sequence}`, feePaid: l.feeShare } : l,
      ),
      nextLeg: { id: 'leg-3', sequence: 3, departureIcao: 'BIRK', arrivalIcao: 'BGBW' },
    })

    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={partway} expanded />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('2 of 6 flown')
    expect(body).toContain('Earned so far')
    expect(body).toContain('$7,000.00')
    expect(body).toContain('Still to earn')
    expect(body).toContain('$15,000.00')
    // The next leg is called out by name, from the server's own nextLeg rather than a local guess.
    expect(body).toContain('Next')

    mounted.unmount()
  })
})

/**
 * A leg can be flown and pay nothing - completed with estimates, or invalidated by slew or a
 * position jump. Saying so quietly on the leg is what stops the totals looking wrong for no visible
 * reason, which is how "Earned so far" came to overstate what had actually been banked.
 */
describe('ContractCard - a leg that flew and was not paid', () => {
  it('shows what the leg PAID, not what it was worth, and says it was not paid', async () => {
    stubSettings()
    const partway = contract({
      ...ferry,
      status: 'Accepted',
      flownLegCount: 2,
      // Only leg 1 reached the ledger.
      earnedSoFar: 2000,
      outstandingFee: 8000,
      legs: ferry.legs!.map((l) => {
        if (l.sequence === 1) return { ...l, flown: true, flightId: 'f-1', feePaid: l.feeShare }
        if (l.sequence === 2) return { ...l, flown: true, flightId: 'f-2', feePaid: 0 }
        return l
      }),
      nextLeg: { id: 'leg-3', sequence: 3, departureIcao: 'BIRK', arrivalIcao: 'BGBW' },
    })

    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={partway} expanded />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('Flown · not paid')
    // Leg 1 flew and paid, so it reads as flown rather than as unpaid.
    expect(body).toContain('Flown')

    mounted.unmount()
  })

  it('does not label an unflown leg as unpaid — "not yet" is not "paid nothing"', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={ferry} />
      </SettingsProvider>,
    )
    await flush()

    expect(text(document.body)).not.toContain('not paid')

    mounted.unmount()
  })
})

/**
 * A bonus nobody knows about cannot influence the decision it exists to influence, so it has to be
 * on the offer - and shown apart from the per-leg fee, because the two are won and lost differently.
 */
describe('ContractCard - the completion bonus', () => {
  it('shows the bonus on an offer, separately from what the legs pay', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={ferry} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('Legs pay')
    expect(body).toContain('$22,000.00')
    expect(body).toContain('+$6,000.00 on finishing')

    mounted.unmount()
  })

  it('shows a plain fee, and no bonus wording, for a job that has none', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={contract()} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('Fee')
    expect(body).not.toContain('on finishing')

    mounted.unmount()
  })

  it('warns on an accepted job that handing back loses the bonus', async () => {
    stubSettings()
    const accepted = contract({ ...ferry, status: 'Accepted', flownLegCount: 1 })
    const mounted = await mount(
      <SettingsProvider>
        <ContractCard contract={accepted} expanded />
      </SettingsProvider>,
    )
    await flush()

    expect(text(document.body)).toContain('you lose it if you hand the job back')

    mounted.unmount()
  })
})

describe('contract scale banding', () => {
  it('separates a single hop from a multi-day chain from an expedition', () => {
    expect(contractScale(contract({ legCount: 1, totalPlannedBlockMinutes: 45 }))).toBe('Short hop')
    expect(contractScale(contract({ legCount: 2, totalPlannedBlockMinutes: 200 }))).toBe('Day’s work')
    expect(contractScale(contract({ legCount: 5 }))).toBe('Multi-day')
    expect(contractScale(contract({ legCount: 11 }))).toBe('Expedition')
  })

  it('treats a long single sector as a day’s work rather than an expedition', () => {
    // Leg count is what makes a job span sessions. One nine-hour sector is still one evening.
    const longHaul = contract({ legCount: 1, totalPlannedBlockMinutes: 540 })
    expect(contractScale(longHaul)).toBe('Day’s work')
    expect(isExpedition(longHaul)).toBe(false)
  })
})
