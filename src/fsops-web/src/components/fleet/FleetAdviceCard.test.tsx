import { beforeEach, describe, expect, it, vi } from 'vitest'

import { FleetAdviceCard } from './FleetAdviceCard'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, getByRole, isDisabled, mount, text } from '@/test/domHarness'
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

const STORAGE_KEY = 'fsops-fleet-advice-expanded'

/** The card is collapsed by default, so every content assertion below has to open it first -
 *  otherwise it would be asserting on text that is present in the DOM but hidden from the player. */
function toggle(container: HTMLElement): HTMLElement {
  return getByRole(container, 'button', { name: /What to buy next/ })
}

function contentPanel(container: HTMLElement): HTMLElement {
  const id = toggle(container).getAttribute('aria-controls')
  const panel = id ? container.querySelector<HTMLElement>(`#${CSS.escape(id)}`) : null
  if (!panel) throw new Error('the toggle names no content panel via aria-controls')
  return panel
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

/** Renders and opens the card, for the tests that are about what the advice SAYS rather than about
 *  the collapsing itself. */
async function renderExpanded(data: FleetAdviceResponse | null, status: 'loading' | 'ready' | 'error' = 'ready') {
  const mounted = await render(data, status)
  click(toggle(mounted.container))
  await flush()
  return mounted
}

beforeEach(() => {
  window.localStorage.clear()
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

/**
 * Collapsed by default is the requirement most likely to regress silently - it only shows up for a
 * player with nothing stored, which is never the state of the machine it was built on. So the
 * default is asserted directly, from an empty localStorage, rather than inferred from the toggle
 * working.
 */
describe('FleetAdviceCard - collapsing', () => {
  it('is collapsed on a first visit, with nothing stored', async () => {
    const { container, unmount } = await render(response())

    expect(toggle(container).getAttribute('aria-expanded')).toBe('false')
    expect(contentPanel(container).hasAttribute('hidden')).toBe(true)
    unmount()
  })

  it('still says what it is and what it found while collapsed, so it can be discovered at all', async () => {
    const { container, unmount } = await render(response({ idleAircraftCount: 2 }))
    const header = text(toggle(container))

    expect(header).toContain('What to buy next')
    expect(header).toContain('2 aircraft idle')
    // The collapsed line is a summary, not the full headline sentence.
    expect(header).not.toContain('Everything you own is working')
    unmount()
  })

  it('leads the collapsed summary with idle aircraft over suggestions - a reason not to spend outranks reasons to spend', async () => {
    const { container, unmount } = await render(response({ idleAircraftCount: 1, suggestions: [suggestion(), suggestion({ aircraftTypeId: 'type-b' })] }))

    expect(text(toggle(container))).toContain('1 aircraft idle')
    unmount()
  })

  it('summarises the suggestions when there is nothing wrong to report', async () => {
    const { container, unmount } = await render(response())

    expect(text(toggle(container))).toContain('1 aircraft suggested')
    unmount()
  })

  it('opens on one click, and the toggle is a real expandable control', async () => {
    const { container, unmount } = await render(response())

    click(toggle(container))
    await flush()

    expect(toggle(container).getAttribute('aria-expanded')).toBe('true')
    expect(contentPanel(container).hasAttribute('hidden')).toBe(false)
    expect(isDisabled(toggle(container))).toBe(false)
    unmount()
  })

  it('remembers being opened, and remembers being closed again', async () => {
    const first = await render(response())
    click(toggle(first.container))
    await flush()
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')
    first.unmount()

    const second = await render(response())
    expect(toggle(second.container).getAttribute('aria-expanded')).toBe('true')

    click(toggle(second.container))
    await flush()
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()
    second.unmount()

    const third = await render(response())
    expect(toggle(third.container).getAttribute('aria-expanded')).toBe('false')
    third.unmount()
  })
})

describe('FleetAdviceCard', () => {
  it('gives every suggestion a reason and a real price to buy or lease', async () => {
    const { container, unmount } = await renderExpanded(response())
    const body = text(container)

    expect(body).toContain('Airbus A330-200')
    expect(body).toContain('opens a route you already have')
    expect(body).toContain('Buy')
    expect(body).toContain('Lease')
    unmount()
  })

  /** A planner that always finds a reason to spend money is not advice. */
  it('leads with rostering what you already own when an aircraft is idle', async () => {
    const { container, unmount } = await renderExpanded(
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
    const { container, unmount } = await renderExpanded(
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
    const { container, unmount } = await renderExpanded(
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
    const { container, unmount } = await renderExpanded(
      response({ suggestions: [suggestion({ monthlyLease: null, leaseDeposit: null, affordableToLeaseNow: false })] }),
    )

    expect(text(container)).toContain('Not leasable')
    unmount()
  })

  it('surfaces a failure rather than pretending there is nothing to advise', async () => {
    const { container, unmount } = await renderExpanded(null, 'error')
    expect(text(container)).toContain('Could not work out any fleet advice')
    unmount()
  })
})
