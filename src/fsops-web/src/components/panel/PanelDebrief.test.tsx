import { beforeEach, describe, expect, it, vi } from 'vitest'

import { PanelDebrief } from './PanelDebrief'
import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { MaintenanceWarning } from '@/lib/panelFormat'
import type { Flight } from '@/types/flight'
import type { FleetAircraftSummary } from '@/types/fleet'
import type { RouteSummary } from '@/types/route'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function flight(overrides: Partial<Flight> = {}): Flight {
  return {
    id: 'flight-1',
    airlineId: 'airline-1',
    routeId: 'route-1',
    fleetAircraftId: 'ac-1',
    pilotId: 'pilot-1',
    status: 'Completed',
    plannedDepartureUtc: '2026-08-11T09:00:00Z',
    plannedBlockMinutes: 75,
    outUtc: '2026-08-11T09:05:00Z',
    offUtc: '2026-08-11T09:15:00Z',
    onUtc: '2026-08-11T10:15:00Z',
    inUtc: '2026-08-11T10:20:00Z',
    paxBooked: 150,
    paxFlown: 148,
    fuelPlannedKg: 4200,
    fuelUsedKg: 4100,
    landingFpmFirst: -142,
    landingFpmHardest: -142,
    landingGForce: 1.24,
    centrelineDeviationM: 1.5,
    titleFlown: 'Airbus A320neo',
    typeMismatch: false,
    simRateElevated: false,
    maxSimulationRateObserved: 1,
    slewDetected: false,
    positionJumpDetected: false,
    revenue: 21000,
    totalCost: 13500,
    createdUtc: '2026-08-11T08:30:00Z',
    ...overrides,
  }
}

const route: RouteSummary = {
  id: 'route-1',
  departureIcao: 'EGGD',
  departureName: 'Bristol',
  arrivalIcao: 'EGPH',
  arrivalName: 'Edinburgh',
  flightNumber: '204',
  returnRouteId: 'route-2',
  distanceNm: 300,
  baseFare: 90,
  isActive: true,
  createdUtc: '2026-01-01T00:00:00Z',
}

async function render(props: Partial<Parameters<typeof PanelDebrief>[0]> = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <PanelDebrief
        flight={props.flight ?? flight()}
        route={props.route === undefined ? route : props.route}
        airlineIcaoCode={props.airlineIcaoCode === undefined ? 'FSO' : props.airlineIcaoCode}
        maintenanceWarning={props.maintenanceWarning ?? null}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('PanelDebrief - the landing', () => {
  it('shows the touchdown rate as a sink rate with its verdict', async () => {
    const { container, unmount } = await render({ flight: flight({ landingFpmFirst: -142 }) })

    expect(text(container)).toContain('-142 fpm')
    expect(text(container)).toContain('Smooth touchdown')

    unmount()
  })

  it('reports a sink rate even when the raw sample arrived positive, rather than a climbing landing', async () => {
    // The sim can report the figure either way round; "+520 fpm" on a debrief would be nonsense.
    const { container, unmount } = await render({ flight: flight({ landingFpmFirst: 520 }) })

    expect(text(container)).toContain('-520 fpm')
    expect(text(container)).toContain('Hard touchdown')

    unmount()
  })

  it('says plainly that no touchdown was captured instead of showing a zero', async () => {
    const { container, unmount } = await render({ flight: flight({ landingFpmFirst: null }) })

    expect(text(container)).toContain('No touchdown was captured.')
    // "0 fpm" would read as a perfect greaser rather than as missing data.
    expect(text(container)).not.toContain('0 fpm')

    unmount()
  })
})

describe('PanelDebrief - what the sector earned', () => {
  it('shows revenue minus cost, not revenue', async () => {
    const { container, unmount } = await render({ flight: flight({ revenue: 21000, totalCost: 13500 }) })

    expect(text(container)).toContain('$7,500.00')
    expect(text(container)).not.toContain('$21,000.00')

    unmount()
  })

  it('shows a loss-making sector as negative, with exactly one minus sign', async () => {
    const { container, unmount } = await render({ flight: flight({ revenue: 9000, totalCost: 14200 }) })

    const body = text(container)
    expect(body).toContain('-$5,200.00')
    // The component prepends its own sign and passes an absolute value to the formatter, which
    // also signs negatives - getting that wrong produces "--$5,200.00".
    expect(body).not.toContain('--')

    unmount()
  })
})

describe('PanelDebrief - block variance', () => {
  it('reads "On plan" when the flight ran to schedule, rather than "+0m"', async () => {
    const { container, unmount } = await render({
      flight: flight({ outUtc: '2026-08-11T09:05:00Z', inUtc: '2026-08-11T10:20:00Z', plannedBlockMinutes: 75 }),
    })

    expect(text(container)).toContain('On plan')

    unmount()
  })

  it('signs an overrun positive and an early arrival negative', async () => {
    const late = await render({
      flight: flight({ outUtc: '2026-08-11T09:05:00Z', inUtc: '2026-08-11T10:35:00Z', plannedBlockMinutes: 75 }),
    })
    expect(text(late.container)).toContain('+15m')
    late.unmount()

    const early = await render({
      flight: flight({ outUtc: '2026-08-11T09:05:00Z', inUtc: '2026-08-11T10:10:00Z', plannedBlockMinutes: 75 }),
    })
    expect(text(early.container)).toContain('-10m')
    early.unmount()
  })
})

describe('PanelDebrief - integrity warnings', () => {
  it('says the sector is not valid for payment when slew was detected', async () => {
    const { container, unmount } = await render({ flight: flight({ slewDetected: true }) })

    // Typographic apostrophe: the component renders &rsquo;, not an ASCII quote.
    expect(text(container)).toContain('This sector wasn’t valid for payment')

    unmount()
  })

  it('says the same for a position jump', async () => {
    const { container, unmount } = await render({ flight: flight({ positionJumpDetected: true }) })

    // Typographic apostrophe: the component renders &rsquo;, not an ASCII quote.
    expect(text(container)).toContain('This sector wasn’t valid for payment')

    unmount()
  })

  it('says nothing about payment validity on an ordinary flight', async () => {
    const { container, unmount } = await render()

    // A warning shown on a clean sector would be worse than one missed: it teaches the player to
    // ignore the line.
    expect(text(container)).not.toContain('valid for payment')

    unmount()
  })

  it('warns that block time is not a measured figure when time acceleration was used', async () => {
    const { container, unmount } = await render({
      flight: flight({ simRateElevated: true, maxSimulationRateObserved: 4 }),
    })

    expect(text(container)).toContain('Time acceleration was used')
    expect(text(container)).toContain('block time above is not a measured figure')

    unmount()
  })
})

describe('PanelDebrief - route and callsign', () => {
  it('shows the route and the callsign built from the airline code', async () => {
    const { container, unmount } = await render()

    expect(text(container)).toContain('EGGD → EGPH')
    expect(text(container)).toContain('FSO204')

    unmount()
  })

  it('degrades to a generic heading when the route could not be resolved, rather than rendering blanks', async () => {
    const { container, unmount } = await render({ route: null })

    expect(text(container)).toContain('Flight complete')
    expect(text(container)).not.toContain('→')

    unmount()
  })
})

describe('PanelDebrief - maintenance warning', () => {
  const warning: MaintenanceWarning = {
    aircraft: { registration: 'G-ABCD' } as FleetAircraftSummary,
    checkType: 'A',
    hoursRemaining: 12.4,
  }

  it('names the aircraft, the hours left and which check when one is due', async () => {
    const { container, unmount } = await render({ maintenanceWarning: warning })

    expect(text(container)).toContain('G-ABCD is 12h from its next A-check.')

    unmount()
  })

  it('shows no maintenance line at all when nothing is close to a check', async () => {
    const { container, unmount } = await render({ maintenanceWarning: null })

    expect(text(container)).not.toContain('from its next')

    unmount()
  })
})
