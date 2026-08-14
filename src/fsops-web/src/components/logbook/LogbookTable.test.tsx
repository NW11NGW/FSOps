import { beforeEach, describe, expect, it, vi } from 'vitest'

import { LogbookTable } from './LogbookTable'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { LogbookSector } from '@/types/flight'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function sector(overrides: Partial<LogbookSector> = {}): LogbookSector {
  return {
    flightId: 'f1',
    status: 'Completed',
    routeId: 'r1',
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

async function render(sectors: LogbookSector[], onSort = vi.fn(), onOpen = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <LogbookTable
        sectors={sectors}
        sortKey="date"
        sortDirection="desc"
        onSort={onSort}
        onOpen={onOpen}
        airlineIcaoCode="TST"
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
    return Promise.reject(new Error(`Unexpected GET ${path}`)) as never
  })
})

describe('LogbookTable', () => {
  it('shows the sector a pilot would look up: route, callsign, aircraft, block time and what it earned', async () => {
    const { unmount } = await render([sector()])

    const body = text(document.body)
    expect(body).toContain('EGGD → EGPH')
    expect(body).toContain('TST101')
    expect(body).toContain('G-TEST')
    expect(body).toContain('1h 30m')
    expect(body).toContain('-150 fpm')
    expect(body).toContain('$6,000.00')

    unmount()
  })

  it('says "Not measured" for a landing the sim never reported a rate for, never 0 fpm', async () => {
    // Conflating "no rate was captured" with a real figure is the exact defect that once showed a
    // greaser as a confident 0 fpm.
    const { unmount } = await render([sector({ landingFpmFirst: null })])

    expect(text(document.body)).toContain('Not measured')
    expect(text(document.body)).not.toContain('0 fpm')

    unmount()
  })

  it('says block time is not measured when the sim ran faster than real time', async () => {
    const { unmount } = await render([sector({ actualBlockMinutes: null, blockTimeNotMeasured: true })])

    expect(text(document.body)).toContain('Not measured')

    unmount()
  })

  it('shows the block-time delta against plan with its direction', async () => {
    const { unmount } = await render([sector({ plannedBlockMinutes: 90, actualBlockMinutes: 102 })])

    expect(text(document.body)).toContain('+12m')

    unmount()
  })

  it('renders a loss as a negative figure rather than a bare number', async () => {
    const { unmount } = await render([sector({ net: -2400 })])

    expect(text(document.body)).toContain('-$2,400.00')

    unmount()
  })

  it('badges a sector that did not complete, and leaves a completed one unbadged', async () => {
    const { unmount } = await render([sector({ flightId: 'a', status: 'Abandoned' }), sector({ flightId: 'b', status: 'Completed' })])

    const body = text(document.body)
    expect(body).toContain('Abandoned')
    expect(body).not.toContain('Completed')

    unmount()
  })

  it('opens the sector when its row is clicked', async () => {
    const onOpen = vi.fn()
    const { unmount } = await render([sector({ flightId: 'clicked' })], vi.fn(), onOpen)

    const row = document.body.querySelector('tbody tr')!
    click(row)

    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ flightId: 'clicked' }))

    unmount()
  })

  it('asks to re-sort when a sortable header is clicked', async () => {
    const onSort = vi.fn()
    const { unmount } = await render([sector()], onSort)

    const netHeader = Array.from(document.body.querySelectorAll('th button')).find((b) => b.textContent?.includes('Net'))!
    click(netHeader)

    expect(onSort).toHaveBeenCalledWith('net')

    unmount()
  })
})
