import { beforeEach, describe, expect, it, vi } from 'vitest'

import { FleetAdviceCard } from './FleetAdviceCard'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FleetAdviceResponse, FleetSuggestion } from '@/types/planning'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function suggestion(overrides: Partial<FleetSuggestion> = {}): FleetSuggestion {
  return {
    aircraftTypeId: 'type-a332',
    typeName: 'Airbus A330-200',
    icaoType: 'A332',
    seats: 260,
    rangeNm: 6100,
    alreadyOwned: false,
    purchasePrice: 490_000,
    monthlyLease: 60_000,
    leaseDeposit: 60_000,
    monthlyInsurance: 6_000,
    affordableToBuyNow: false,
    affordableToLeaseNow: true,
    unlocksRouteCount: 1,
    unlocksOpportunityCount: 2,
    extraSeatsOnBusyRoutes: 0,
    bestSector: 'EGGD-KJFK',
    bestSectorProfit: 18_400,
    reason: 'The Airbus A330-200 (260 seats, 6,100 nm) opens a route you already have but can’t fly.',
    ...overrides,
  }
}

function response(overrides: Partial<FleetAdviceResponse> = {}): FleetAdviceResponse {
  return {
    cashBalance: 120_000,
    fleetSize: 1,
    idleAircraftCount: 0,
    headline: 'Everything you own is working. More capacity is a growth decision now, not a fix.',
    utilisation: [
      {
        fleetAircraftId: 'ac-1',
        registration: 'G-TEST',
        typeName: 'Airbus A320',
        seats: 180,
        locationIcao: 'EGGD',
        status: 'Active',
        reservedForPlayer: true,
        scheduledSectorsPerWeek: 4,
      },
    ],
    unflyableRoutes: [],
    seatCappedRoutes: [],
    suggestions: [suggestion()],
    ...overrides,
  }
}

async function render(data: FleetAdviceResponse | null, status: 'loading' | 'ready' | 'error' = 'ready') {
  const mounted = await mount(
    <SettingsProvider>
      <FleetAdviceCard data={data} status={status} isRefreshing={false} onAcquire={vi.fn()} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('FleetAdviceCard', () => {
  it('gives every suggestion a reason and a real price to buy or lease', async () => {
    const { container, unmount } = await render(response())
    const body = text(container)

    expect(body).toContain('Airbus A330-200')
    expect(body).toContain('opens a route you already have')
    expect(body).toContain('Buy')
    expect(body).toContain('Lease')
    unmount()
  })

  /** A planner that always finds a reason to spend money is not advice. */
  it('leads with rostering what you already own when an aircraft is idle', async () => {
    const { container, unmount } = await render(
      response({
        idleAircraftCount: 1,
        headline: '1 aircraft has nothing scheduled. Rostering what you already own earns more than buying another airframe.',
        utilisation: [
          {
            fleetAircraftId: 'ac-1',
            registration: 'G-IDLE',
            typeName: 'Airbus A320',
            seats: 180,
            locationIcao: 'EGPH',
            status: 'Active',
            reservedForPlayer: false,
            scheduledSectorsPerWeek: 0,
          },
        ],
      }),
    )
    const body = text(container)

    expect(body).toContain('Rostering what you already own')
    expect(body).toContain('G-IDLE at EGPH')
    unmount()
  })

  it('names the routes nothing owned can fly, with the reason', async () => {
    const { container, unmount } = await render(
      response({
        unflyableRoutes: [
          {
            routeId: 'r-1',
            departureIcao: 'EGGD',
            arrivalIcao: 'KJFK',
            distanceNm: 2930,
            reason: '2,930 nm is beyond every aircraft you own.',
          },
        ],
      }),
    )

    expect(text(container)).toContain('2,930 nm is beyond every aircraft you own.')
    unmount()
  })

  it('quantifies routes turning passengers away rather than just flagging them', async () => {
    const { container, unmount } = await render(
      response({
        seatCappedRoutes: [
          {
            routeId: 'r-2',
            departureIcao: 'EGGD',
            arrivalIcao: 'EGPH',
            marketDemandPax: 245,
            seats: 180,
            typeName: 'Airbus A320',
            turnedAwayPerSector: 65,
          },
        ],
      }),
    )
    const body = text(container)

    expect(body).toContain('245 want to fly it')
    expect(body).toContain('65 a sector go unsold')
    unmount()
  })

  it('says plainly when a type cannot be leased instead of showing a made-up rate', async () => {
    const { container, unmount } = await render(
      response({ suggestions: [suggestion({ monthlyLease: null, leaseDeposit: null, affordableToLeaseNow: false })] }),
    )

    expect(text(container)).toContain('Not leasable')
    unmount()
  })

  it('surfaces a failure rather than pretending there is nothing to advise', async () => {
    const { container, unmount } = await render(null, 'error')
    expect(text(container)).toContain('Could not work out any fleet advice')
    unmount()
  })
})
