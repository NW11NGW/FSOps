import { beforeEach, describe, expect, it, vi } from 'vitest'

import { RoutesTable } from './RoutesTable'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, getByRole, mount, queryAllByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { RouteSummary } from '@/types/route'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function route(overrides: Partial<RouteSummary> = {}): RouteSummary {
  return {
    id: 'r-out',
    departureIcao: 'EGGD',
    departureName: 'Bristol Airport',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh Airport',
    flightNumber: '101',
    returnRouteId: 'r-in',
    distanceNm: 280,
    baseFare: 90,
    isActive: true,
    createdUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

const outbound = route()
const inbound = route({
  id: 'r-in',
  departureIcao: 'EGPH',
  departureName: 'Edinburgh Airport',
  arrivalIcao: 'EGGD',
  arrivalName: 'Bristol Airport',
  flightNumber: '102',
  returnRouteId: 'r-out',
})

interface RenderOptions {
  routes?: RouteSummary[]
  status?: 'loading' | 'ready' | 'error'
  blockMinutes?: Record<string, number | undefined>
  selectedId?: string | null
  hoveredId?: string | null
  airlineIcaoCode?: string | null
  onSelect?: (route: RouteSummary) => void
  onDelete?: (route: RouteSummary) => Promise<void>
  onHover?: (id: string | null) => void
}

async function render(options: RenderOptions = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <RoutesTable
        routes={options.routes ?? [outbound, inbound]}
        status={options.status ?? 'ready'}
        blockMinutes={options.blockMinutes ?? { [outbound.id]: 65, [inbound.id]: 68 }}
        selectedId={options.selectedId ?? null}
        hoveredId={options.hoveredId ?? null}
        airlineIcaoCode={options.airlineIcaoCode ?? 'TST'}
        onSelect={options.onSelect ?? vi.fn()}
        onDelete={options.onDelete ?? vi.fn().mockResolvedValue(undefined)}
        onHover={options.onHover}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('RoutesTable - loading, error and empty', () => {
  it('shows no rows while loading', async () => {
    const { container, unmount } = await render({ status: 'loading', routes: [] })

    expect(queryAllByRole(container, 'row')).toHaveLength(0)
    expect(text(container)).not.toContain('No routes yet')

    unmount()
  })

  it('distinguishes a failed load from an empty route list', async () => {
    const { container, unmount } = await render({ status: 'error', routes: [] })

    expect(text(container)).toContain('Could not load your routes')
    expect(text(container)).not.toContain('No routes yet')

    unmount()
  })

  it('says how to build the first route when there are none yet', async () => {
    const { container, unmount } = await render({ status: 'ready', routes: [] })

    expect(text(container)).toContain('No routes yet')

    unmount()
  })
})

describe('RoutesTable - pairing outbound and return legs', () => {
  it('shows one row per round-trip pair, not one per leg', async () => {
    const { container, unmount } = await render()

    // Header row plus exactly one pair row for the outbound/inbound pair.
    expect(queryAllByRole(container, 'row')).toHaveLength(2)

    unmount()
  })

  it('shows both legs’ callsigns, composed from the airline ICAO code', async () => {
    const { container, unmount } = await render()

    const body = text(container)
    expect(body).toContain('TST101')
    expect(body).toContain('TST102')

    unmount()
  })

  it('reveals both legs’ own figures only once expanded', async () => {
    const { container, unmount } = await render()

    expect(text(container)).not.toContain('Return leg not created yet')
    expect(text(container)).not.toMatch(/Outbound.*EGGD.*EGPH/s)

    click(getByRole(container, 'button', { name: 'Expand leg details' }))

    const body = text(container)
    expect(body).toContain('Outbound')
    expect(body).toContain('Return')

    unmount()
  })

  it('flags a route missing its return leg, and never blocks showing the outbound leg', async () => {
    const solo = route({ id: 'r-solo', returnRouteId: null, flightNumber: '201' })
    const { container, unmount } = await render({
      routes: [solo],
      blockMinutes: { [solo.id]: 40 },
    })

    // The pair still renders (nothing here blocks display) - only one row for the lone leg.
    expect(queryAllByRole(container, 'row')).toHaveLength(2)

    click(getByRole(container, 'button', { name: 'Expand leg details' }))
    expect(text(container)).toContain('Return leg not created yet')

    unmount()
  })

  it('shows a single fare when both legs charge the same, and both when they differ', async () => {
    const { container: same, unmount: unmountSame } = await render()
    expect(text(same)).toContain('$90.00')
    expect(text(same)).not.toContain('/')
    unmountSame()

    const differentFareInbound = { ...inbound, baseFare: 95 }
    const { container: different, unmount: unmountDifferent } = await render({ routes: [outbound, differentFareInbound] })
    const body = text(different)
    expect(body).toContain('$90.00')
    expect(body).toContain('$95.00')
    unmountDifferent()
  })

  it('shows each leg’s block time once it resolves, keyed by that leg’s own id', async () => {
    const { container, unmount } = await render({ blockMinutes: { [outbound.id]: 65 } })

    expect(text(container)).toContain('1h 5m')

    unmount()
  })
})

describe('RoutesTable - selecting a route', () => {
  it('calls onSelect with the outbound leg when the pair row is clicked', async () => {
    const onSelect = vi.fn()
    const { container, unmount } = await render({ onSelect })

    const pairRow = container.querySelector<HTMLElement>('tbody tr[role="button"]')
    if (!pairRow) throw new Error('pair row not found')
    click(pairRow)

    expect(onSelect).toHaveBeenCalledWith(outbound)

    unmount()
  })

  it('marks the pair as selected when either leg’s id matches selectedId', async () => {
    const { container, unmount } = await render({ selectedId: inbound.id })

    const pairRow = container.querySelector<HTMLElement>('tbody tr[role="button"]')
    if (!pairRow) throw new Error('pair row not found')
    expect(pairRow.getAttribute('aria-pressed')).toBe('true')

    unmount()
  })

  it('does not mark the pair selected when selectedId matches neither leg', async () => {
    const { container, unmount } = await render({ selectedId: 'some-other-route' })

    const pairRow = container.querySelector<HTMLElement>('tbody tr[role="button"]')
    if (!pairRow) throw new Error('pair row not found')
    expect(pairRow.getAttribute('aria-pressed')).toBe('false')

    unmount()
  })

  it('lets a leg inside the expanded detail be selected directly', async () => {
    const onSelect = vi.fn()
    const { container, unmount } = await render({ onSelect })

    click(getByRole(container, 'button', { name: 'Expand leg details' }))
    click(findButton(container, 'Return'))

    expect(onSelect).toHaveBeenCalledWith(inbound)

    unmount()
  })
})

describe('RoutesTable - deleting a pair', () => {
  it('names both legs in the confirmation and removes them together on confirm', async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined)
    const { container, unmount } = await render({ onDelete })

    click(getByRole(container, 'button', { name: `Delete route ${outbound.departureIcao} to ${outbound.arrivalIcao} and its return leg` }))
    await flush()

    expect(text(document.body)).toContain('Both legs will be removed')
    expect(text(document.body)).toContain(`${outbound.departureIcao} → ${outbound.arrivalIcao}`)
    expect(text(document.body)).toContain(`${inbound.departureIcao} → ${inbound.arrivalIcao}`)

    click(findButton(document.body, 'Delete both legs'))
    await flush()

    expect(onDelete).toHaveBeenCalledWith(outbound)

    unmount()
  })

  it('names only the one leg when there is no return leg to remove', async () => {
    const solo = route({ id: 'r-solo', returnRouteId: null })
    const onDelete = vi.fn().mockResolvedValue(undefined)
    const { container, unmount } = await render({ routes: [solo], onDelete })

    click(getByRole(container, 'button', { name: `Delete route ${solo.departureIcao} to ${solo.arrivalIcao} and its return leg` }))
    await flush()

    expect(text(document.body)).not.toContain('Both legs will be removed')
    expect(text(document.body)).toContain(`${solo.departureIcao} → ${solo.arrivalIcao} will be removed`)

    unmount()
  })

  it('cancels without deleting anything', async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined)
    const { container, unmount } = await render({ onDelete })

    click(getByRole(container, 'button', { name: `Delete route ${outbound.departureIcao} to ${outbound.arrivalIcao} and its return leg` }))
    await flush()

    click(findButton(document.body, 'Cancel'))
    await flush()

    expect(onDelete).not.toHaveBeenCalled()

    unmount()
  })
})
