import { beforeEach, describe, expect, it, vi } from 'vitest'

import { RouteSelector } from './RouteSelector'
import type { AircraftOptionRow, RoutePairRow, RouteRow } from './routeRow'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, mount, queryAllByRole, text, typeInto } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function aircraft(overrides: Partial<AircraftOptionRow> = {}): AircraftOptionRow {
  return {
    fleetAircraftId: 'ac-1',
    registration: 'G-ABCD',
    aircraftTypeId: 'type-1',
    aircraftTypeName: 'Airbus A320neo',
    paxCapacity: 180,
    estimatedBlockMinutes: 75,
    isFlyable: true,
    reason: null,
    icaoType: 'A20N',
    family: 'A320',
    ...overrides,
  }
}

function routeRow(overrides: Partial<RouteRow> = {}): RouteRow {
  return {
    routeId: 'route-1',
    flightNumber: '204',
    departureIcao: 'EGGD',
    departureName: 'Bristol',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh',
    distanceNm: 300,
    blockMinutes: 75,
    baseFare: 90,
    isFlyable: true,
    reason: null,
    aircraftOptions: [aircraft()],
    aircraftUnknown: false,
    ...overrides,
  }
}

function pair(overrides: Partial<RoutePairRow> = {}): RoutePairRow {
  const active = overrides.active ?? routeRow()
  return {
    pairId: active.routeId,
    active,
    other: null,
    activeIsReturn: false,
    isFlyable: active.isFlyable,
    reason: active.isFlyable ? null : (active.reason ?? 'No aircraft is at this departure airport.'),
    headerDepartureIcao: active.departureIcao,
    headerArrivalIcao: active.arrivalIcao,
    ...overrides,
  }
}

async function render(props: Partial<Parameters<typeof RouteSelector>[0]> = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <RouteSelector
        pairs={props.pairs ?? [pair()]}
        selectedPairId={props.selectedPairId ?? null}
        onSelect={props.onSelect ?? vi.fn()}
        optionsUnavailable={props.optionsUnavailable ?? false}
        airlineIcaoCode={props.airlineIcaoCode === undefined ? 'FSO' : props.airlineIcaoCode}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('RouteSelector - empty and unavailable states', () => {
  it('shows an empty state pointing at the Routes page when there are no routes at all', async () => {
    const { container, unmount } = await render({ pairs: [] })
    const body = text(container)
    expect(body).toContain('No routes yet')
    expect(body).toContain('Build a route on the Routes page')
    unmount()
  })

  it('explains that availability is unknown and stops promoting anything to "Ready now" when GET /flights/options is unavailable', async () => {
    const flyablePair = pair({ active: routeRow({ isFlyable: true, aircraftOptions: [aircraft({ isFlyable: true })] }) })
    const { container, unmount } = await render({ pairs: [flyablePair], optionsUnavailable: true })
    const body = text(container)
    // Typographic apostrophe: the component renders &rsquo;, not an ASCII quote.
    expect(body).toContain('Aircraft availability isn’t available yet')
    expect(body).not.toContain('Ready now')
    unmount()
  })
})

describe('RouteSelector - bucketing', () => {
  it('puts a pair with a flyable aircraft physically ready under "Ready now"', async () => {
    const readyPair = pair({
      pairId: 'ready',
      active: routeRow({ routeId: 'ready', isFlyable: true, aircraftOptions: [aircraft({ isFlyable: true })] }),
    })
    const { container, unmount } = await render({ pairs: [readyPair] })
    const body = text(container)
    expect(body).toContain('Ready now')
    unmount()
  })

  it('puts a flyable pair with no aircraft physically there yet under the plain flyable bucket, not "Ready now"', async () => {
    const flyableButNotParked = pair({
      pairId: 'flyable-not-parked',
      active: routeRow({ routeId: 'flyable-not-parked', isFlyable: true, aircraftOptions: [] }),
    })
    const { container, unmount } = await render({ pairs: [flyableButNotParked] })
    const body = text(container)
    expect(body).not.toContain('Ready now')
    expect(body).toContain('EGGD')
    unmount()
  })

  it('shows the reason and a "Not flyable" badge for a pair that cannot be flown at all', async () => {
    const notFlyable = pair({
      pairId: 'grounded',
      active: routeRow({ routeId: 'grounded', isFlyable: false, aircraftOptions: [], reason: 'No fuel priced at this airport.' }),
      isFlyable: false,
      reason: 'No fuel priced at this airport.',
    })
    const { container, unmount } = await render({ pairs: [notFlyable] })
    const body = text(container)
    expect(body).toContain('Not flyable right now')
    expect(body).toContain('No fuel priced at this airport.')
    expect(body).toContain('Not flyable')
    unmount()
  })
})

describe('RouteSelector - selecting a route', () => {
  it('calls onSelect with the clicked pair', async () => {
    const onSelect = vi.fn()
    const target = pair({ pairId: 'target', active: routeRow({ routeId: 'target', departureIcao: 'LFPG', arrivalIcao: 'EGLL' }) })
    const { container, unmount } = await render({ pairs: [target], onSelect })

    const card = queryAllByRole(container, 'button').find((b) => b.textContent?.includes('LFPG'))
    expect(card).toBeDefined()
    click(card!)

    expect(onSelect).toHaveBeenCalledWith(target)
    unmount()
  })
})

describe('RouteSelector - callsign and search', () => {
  it('shows the callsign built from the airline code next to a route with a flight number', async () => {
    const { container, unmount } = await render({ airlineIcaoCode: 'OLA', pairs: [pair({ active: routeRow({ flightNumber: '502' }) })] })
    expect(text(container)).toContain('OLA502')
    unmount()
  })

  it('filters the list as the search box is typed into, once there are enough routes to show it', async () => {
    // The search input only renders once there are more than 5 pairs.
    const pairs = [
      pair({ pairId: '1', active: routeRow({ routeId: '1', departureIcao: 'EGGD', arrivalIcao: 'EGPH' }) }),
      pair({ pairId: '2', active: routeRow({ routeId: '2', departureIcao: 'EGLL', arrivalIcao: 'EDDF' }) }),
      pair({ pairId: '3', active: routeRow({ routeId: '3', departureIcao: 'LFPG', arrivalIcao: 'EHAM' }) }),
      pair({ pairId: '4', active: routeRow({ routeId: '4', departureIcao: 'LEBL', arrivalIcao: 'LIRF' }) }),
      pair({ pairId: '5', active: routeRow({ routeId: '5', departureIcao: 'EDDM', arrivalIcao: 'LOWW' }) }),
      pair({ pairId: '6', active: routeRow({ routeId: '6', departureIcao: 'KJFK', arrivalIcao: 'KLAX' }) }),
    ]
    const { container, unmount } = await render({ pairs })

    const searchInputs = queryAllByRole(container, 'textbox', { name: 'Search routes' })
    expect(searchInputs).toHaveLength(1)

    typeInto(searchInputs[0] as HTMLInputElement, 'LEBL')

    const body = text(container)
    expect(body).toContain('LEBL')
    expect(body).not.toContain('EGGD')
    unmount()
  })

  it('says plainly that nothing matched rather than showing an empty list', async () => {
    const pairs = [
      pair({ pairId: '1', active: routeRow({ routeId: '1', departureIcao: 'EGGD', arrivalIcao: 'EGPH' }) }),
      pair({ pairId: '2', active: routeRow({ routeId: '2', departureIcao: 'EGLL', arrivalIcao: 'EDDF' }) }),
      pair({ pairId: '3', active: routeRow({ routeId: '3', departureIcao: 'LFPG', arrivalIcao: 'EHAM' }) }),
      pair({ pairId: '4', active: routeRow({ routeId: '4', departureIcao: 'LEBL', arrivalIcao: 'LIRF' }) }),
      pair({ pairId: '5', active: routeRow({ routeId: '5', departureIcao: 'EDDM', arrivalIcao: 'LOWW' }) }),
      pair({ pairId: '6', active: routeRow({ routeId: '6', departureIcao: 'KJFK', arrivalIcao: 'KLAX' }) }),
    ]
    const { container, unmount } = await render({ pairs })

    const searchInput = queryAllByRole(container, 'textbox', { name: 'Search routes' })[0] as HTMLInputElement
    typeInto(searchInput, 'ZZZZ nothing matches this')

    expect(text(container)).toContain('No routes match')
    unmount()
  })
})
