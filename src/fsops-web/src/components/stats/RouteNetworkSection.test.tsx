import { beforeEach, describe, expect, it, vi } from 'vitest'

import { RouteNetworkSection } from './RouteNetworkSection'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FinanceRoute } from '@/types/finance'
import type { RouteSummary } from '@/types/route'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

// The map needs a real WebGL context, which jsdom does not have. What these tests are about is
// whether the colours are EXPLAINED - the legend, the average they are judged against, and the
// sentence behind each one - all of which is ordinary DOM alongside the map.
vi.mock('@/components/map/NetworkMap', () => ({
  NetworkMap: () => <div data-testid="network-map" />,
  default: () => <div data-testid="network-map" />,
}))

function route(overrides: Partial<RouteSummary> = {}): RouteSummary {
  return {
    id: 'route-out',
    departureIcao: 'EGGD',
    departureName: 'Bristol',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh',
    flightNumber: '101',
    returnRouteId: null,
    distanceNm: 280,
    baseFare: 90,
    isActive: true,
    createdUtc: '2026-07-01T00:00:00Z',
    ...overrides,
  }
}

function financeRoute(overrides: Partial<FinanceRoute> = {}): FinanceRoute {
  return {
    routeId: 'route-out',
    departureIcao: 'EGGD',
    arrivalIcao: 'EGPH',
    flightNumber: '101',
    sectorsFlown: 10,
    revenue: 100000,
    cost: 60000,
    profit: 40000,
    paxFlown: 1200,
    seatsFlown: 1800,
    loadFactorPercent: 66.7,
    ...overrides,
  }
}

async function render(routes: RouteSummary[], financeRoutes: FinanceRoute[]) {
  const mounted = await mount(
    <SettingsProvider>
      <RouteNetworkSection
        routes={routes}
        routesLoading={false}
        financeRoutes={financeRoutes}
        homeAirportIcao="EGGD"
        periodDays={30}
        loading={false}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockReset()
  vi.mocked(get).mockImplementation((path: string) => {
    const settings = settingsResponseFor(path)
    if (settings !== undefined) return Promise.resolve(settings) as never
    if (path.startsWith('/airports/')) {
      const icao = path.split('/').pop()
      return Promise.resolve({ icao, name: icao, latitude: 51.4, longitude: -2.7 }) as never
    }
    return Promise.reject(new Error(`Unexpected GET ${path}`)) as never
  })
})

describe('RouteNetworkSection', () => {
  it('states which measure the colours mean and why that measure was chosen', async () => {
    const { unmount } = await render([route()], [financeRoute()])

    const body = text(document.body)
    expect(body).toContain('profit per sector')
    expect(body).toContain('what one more flight on that route adds to the bank')
    expect(body).toContain('aircraft time is what you are actually spending')

    unmount()
  })

  it('spells out what "average" means, with the actual figure', async () => {
    // A legend that says "below average" without saying which average is a black box. 40,000 over
    // 10 sectors is 4,000 a sector.
    const { unmount } = await render([route()], [financeRoute()])

    const body = text(document.body)
    expect(body).toContain('your own network average of $4,000.00 per sector')
    expect(body).toContain('total profit over total sectors flown')

    unmount()
  })

  it('says there is no average to compare against when nothing flew', async () => {
    const { unmount } = await render([route()], [])

    expect(text(document.body)).toContain('there is no average to compare against yet')

    unmount()
  })

  it('justifies a route colour in one sentence when it is picked', async () => {
    const { unmount } = await render(
      [
        route({ id: 'good', departureIcao: 'EGGD', arrivalIcao: 'EGPH' }),
        route({ id: 'bad', departureIcao: 'EGGD', arrivalIcao: 'EGSS' }),
      ],
      [
        financeRoute({ routeId: 'good', sectorsFlown: 10, revenue: 100000, cost: 60000, profit: 40000 }),
        financeRoute({ routeId: 'bad', departureIcao: 'EGGD', arrivalIcao: 'EGSS', sectorsFlown: 5, revenue: 20000, cost: 45000, profit: -25000 }),
      ],
    )

    click(findButton(document.body, 'EGGD ↔ EGSS'))

    const body = text(document.body)
    expect(body).toContain('Loses $5,000.00 every sector')
    expect(body).toContain('flying it again costs you money')

    unmount()
  })

  it('lists a route that was never flown, and says so rather than colouring it as a loss', async () => {
    const { unmount } = await render([route({ id: 'unflown', departureIcao: 'EGGD', arrivalIcao: 'EGPF' })], [])

    click(findButton(document.body, 'EGGD ↔ EGPF'))

    expect(text(document.body)).toContain('No sectors flown on this route in this window')
    expect(text(document.body)).toContain('nothing to judge it on yet')

    unmount()
  })

  it('shows each direction separately, because a pair can earn one way and lose the other', async () => {
    const { unmount } = await render(
      [
        route({ id: 'out', departureIcao: 'EGGD', arrivalIcao: 'EGPH', returnRouteId: 'back' }),
        route({ id: 'back', departureIcao: 'EGPH', arrivalIcao: 'EGGD', returnRouteId: 'out' }),
      ],
      [
        financeRoute({ routeId: 'out', sectorsFlown: 6, revenue: 60000, cost: 20000, profit: 40000 }),
        financeRoute({ routeId: 'back', departureIcao: 'EGPH', arrivalIcao: 'EGGD', sectorsFlown: 6, revenue: 20000, cost: 45000, profit: -25000 }),
      ],
    )

    click(findButton(document.body, 'EGGD ↔ EGPH'))

    const body = text(document.body)
    expect(body).toContain('By direction')
    expect(body).toContain('EGGD → EGPH')
    expect(body).toContain('EGPH → EGGD')
    expect(body).toContain('$40,000.00')
    expect(body).toContain('-$25,000.00')

    unmount()
  })

  it('points at the Routes page when there is no network yet', async () => {
    const { unmount } = await render([], [])

    expect(text(document.body)).toContain('No routes yet')
    expect(text(document.body)).toContain('Build a route on the Routes page')

    unmount()
  })
})
