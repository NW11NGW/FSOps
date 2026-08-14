import { beforeEach, describe, expect, it, vi } from 'vitest'

import { RoutesPnlSection } from './RoutesPnlSection'
import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, queryAllByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FinanceRoute } from '@/types/finance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function route(overrides: Partial<FinanceRoute> = {}): FinanceRoute {
  return {
    routeId: 'route-1',
    departureIcao: 'EGGD',
    arrivalIcao: 'EGPH',
    flightNumber: '204',
    sectorsFlown: 12,
    revenue: 220000,
    cost: 165000,
    profit: 55000,
    paxFlown: 1440,
    seatsFlown: 2160,
    loadFactorPercent: 66.7,
    ...overrides,
  }
}

async function render(status: 'loading' | 'ready' | 'error', routes: FinanceRoute[], periodDays = 30) {
  const mounted = await mount(
    <SettingsProvider>
      <RoutesPnlSection status={status} routes={routes} periodDays={periodDays} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('RoutesPnlSection - states', () => {
  it('shows no rows while loading', async () => {
    const { container, unmount } = await render('loading', [])

    expect(queryAllByRole(container, 'row')).toHaveLength(0)
    expect(text(container)).not.toContain('No completed flights yet')

    unmount()
  })

  it('distinguishes a failed load from having flown nothing', async () => {
    const { container, unmount } = await render('error', [])

    expect(text(container)).toContain('Could not load route P&L.')
    expect(text(container)).not.toContain('No completed flights yet')

    unmount()
  })

  it('says why the table is empty in terms the player can act on', async () => {
    const { container, unmount } = await render('ready', [])

    expect(text(container)).toContain('No completed flights yet')
    expect(text(container)).toContain('Fly a route, or wait for a virtual pilot to')

    unmount()
  })
})

describe('RoutesPnlSection - the figures', () => {
  it('names the window the figures cover', async () => {
    const { container, unmount } = await render('ready', [route()], 7)

    // The endpoint takes an arbitrary ?days=, so a bare "revenue" column with no stated window
    // would be uninterpretable.
    expect(text(container)).toContain('Last 7 days')

    unmount()
  })

  it('shows revenue, cost and profit as three separate figures per route', async () => {
    const { container, unmount } = await render('ready', [route()])

    const body = text(container)
    expect(body).toContain('EGGD → EGPH')
    expect(body).toContain('$220,000.00')
    expect(body).toContain('$165,000.00')
    expect(body).toContain('$55,000.00')

    unmount()
  })

  it('shows a loss-making route as a negative profit rather than an unsigned number', async () => {
    const { container, unmount } = await render('ready', [route({ revenue: 90000, cost: 137000, profit: -47000 })])

    // "This sector loses money" is exactly the fact this table exists to surface; a dropped minus
    // sign would turn the worst route in the network into the third-best.
    expect(text(container)).toContain('-$47,000.00')

    unmount()
  })

  it('shows the flight number when the route has one, and omits the line when it does not', async () => {
    const numbered = await render('ready', [route({ flightNumber: '204' })])
    expect(text(numbered.container)).toContain('204')
    numbered.unmount()

    const unnumbered = await render('ready', [route({ flightNumber: null })])
    expect(text(unnumbered.container)).not.toContain('null')
    unnumbered.unmount()
  })

  it('lists one row per route plus the header row', async () => {
    const { container, unmount } = await render('ready', [
      route(),
      route({ routeId: 'route-2', departureIcao: 'EGPH', arrivalIcao: 'EGGD' }),
    ])

    expect(queryAllByRole(container, 'row')).toHaveLength(3)

    unmount()
  })
})
