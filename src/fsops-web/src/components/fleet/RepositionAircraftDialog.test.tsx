import { beforeEach, describe, expect, it, vi } from 'vitest'

import { RepositionAircraftDialog } from './RepositionAircraftDialog'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, getByRole, isDisabled, mount, text } from '@/test/domHarness'
import type { FleetAircraftSummary, RepositionOptions } from '@/types/fleet'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post } from '@/lib/api'

const aircraft: FleetAircraftSummary = {
  id: 'ac-1',
  registration: 'G-ABCD',
  aircraftTypeId: 'type-1',
  aircraftTypeName: 'A320-200',
  family: 'A320',
  paxCapacity: 180,
  ownership: 'Owned',
  status: 'Active',
  locationIcao: 'EGSS',
  airframeHours: 12000,
  hoursSinceACheck: 100,
  hoursSinceCCheck: 500,
  hoursToNextACheck: 400,
  hoursToNextCCheck: 4500,
  conditionPercent: 82,
  fuelOnBoardKg: 5000,
  groundedUntilUtc: null,
  groundedReason: null,
  reservedForPlayer: true,
  createdUtc: '2026-01-01T00:00:00Z',
}

const options: RepositionOptions = {
  fleetAircraftId: 'ac-1',
  registration: 'G-ABCD',
  aircraftTypeName: 'A320-200',
  currentIcao: 'EGSS',
  currentAirportName: 'London Stansted Airport',
  cost: 2000,
  cashBalance: 60000,
  cashAfter: 58000,
  destinations: [
    { icao: 'EGGD', name: 'Bristol Airport', municipality: 'Bristol', routeCount: 4 },
    { icao: 'EGPH', name: 'Edinburgh Airport', municipality: 'Edinburgh', routeCount: 2 },
  ],
  canReposition: true,
  blockReason: null,
}

function mockGet(optionsOverride: RepositionOptions = options) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return {
        currencyCode: 'USD', distanceUnit: 'Nm', altitudeUnit: 'Feet', weightUnit: 'Kg',
        timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path.includes('/reposition-options')) return optionsOverride as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

async function open(props: Partial<Parameters<typeof RepositionAircraftDialog>[0]> = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <RepositionAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} {...props} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  mockGet()
  vi.mocked(post).mockReset()
})

describe('RepositionAircraftDialog - confirmation', () => {
  it('never posts on a single click: no destination is pre-selected, so the confirm button starts disabled', async () => {
    // The whole guard against an accidental 2,000 spend. If a destination were ever pre-selected,
    // one stray click on a live confirm button would charge the player.
    const mounted = await open()

    expect(isDisabled(findButton(document.body, 'Select an airport'))).toBe(true)
    expect(post).not.toHaveBeenCalled()

    mounted.unmount()
  })

  it('shows the aircraft, both airports, the cost and the resulting balance before charging anything', async () => {
    const mounted = await open()

    click(getByRole(document.body, 'button', { name: /EGGD/ }))
    await flush()

    const body = text(document.body)
    expect(body).toContain('G-ABCD')
    expect(body).toContain('EGSS')
    expect(body).toContain('EGGD')
    // Money always through the app's formatter, never a bare number.
    expect(body).toContain('$2,000.00')
    expect(body).toContain('$58,000.00')
    expect(post).not.toHaveBeenCalled()

    mounted.unmount()
  })

  it('sends the chosen destination and the exact cost the player was shown', async () => {
    vi.mocked(post).mockResolvedValue({
      fleetAircraftId: 'ac-1', registration: 'G-ABCD', fromIcao: 'EGSS', toIcao: 'EGPH', cost: 2000, cashBalance: 58000,
    })
    const onSuccess = vi.fn()
    const mounted = await open({ onSuccess })

    click(getByRole(document.body, 'button', { name: /EGPH/ }))
    await flush()

    click(findButton(document.body, 'Move to EGPH'))
    await flush()

    expect(post).toHaveBeenCalledWith('/fleet/ac-1/reposition', { destinationIcao: 'EGPH', expectedCost: 2000 })
    expect(onSuccess).toHaveBeenCalled()

    mounted.unmount()
  })
})

describe('RepositionAircraftDialog - destinations', () => {
  it('offers only the airports the server returned, and never the one the aircraft is already at', async () => {
    const mounted = await open()

    const destinations = ['EGGD', 'EGPH'].map((icao) => getByRole(document.body, 'button', { name: new RegExp(icao) }))
    expect(destinations).toHaveLength(2)
    // EGSS is where the aircraft is - it must be shown as the origin, never offered as a target.
    expect(document.body.querySelectorAll('[role="radio"]')).toHaveLength(2)

    mounted.unmount()
  })

  it('states the refusal verbatim and offers no confirm button at all when the move is blocked', async () => {
    // Server-authored wording, rendered as-is: every refusal is written to end in an action that
    // works, and a paraphrase here would let that drift.
    const blockReason =
      'G-ABCD is available to virtual pilots, and only aircraft reserved for you can be repositioned. ' +
      'Reserve it for yourself from the Fleet page (the "Reserve for you" button on its row), then move it.'
    mockGet({ ...options, canReposition: false, destinations: [], blockReason })

    const mounted = await open()

    expect(text(document.body)).toContain(blockReason)
    expect(document.body.querySelectorAll('[role="radio"]')).toHaveLength(0)
    expect(() => findButton(document.body, 'Move to')).toThrow()

    mounted.unmount()
  })
})

describe('RepositionAircraftDialog - reset behaviour', () => {
  it('keeps the chosen destination when the parent re-renders with the same aircraft id', async () => {
    // Fleet.tsx builds `aircraft` as a fresh object literal on every render (live cash heartbeat),
    // so a reference-identity effect dependency would silently clear the player's choice.
    const mounted = await open()

    click(getByRole(document.body, 'button', { name: /EGPH/ }))
    await flush()
    expect(text(findButton(document.body, 'Move to EGPH'))).toContain('EGPH')

    await mounted.rerender(
      <SettingsProvider>
        <RepositionAircraftDialog aircraft={{ ...aircraft }} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(text(findButton(document.body, 'Move to EGPH'))).toContain('EGPH')

    mounted.unmount()
  })

  it('clears a destination chosen for a DIFFERENT aircraft', async () => {
    const mounted = await open()

    click(getByRole(document.body, 'button', { name: /EGPH/ }))
    await flush()

    mockGet({ ...options, fleetAircraftId: 'ac-2', registration: 'N12345' })
    await mounted.rerender(
      <SettingsProvider>
        <RepositionAircraftDialog
          aircraft={{ ...aircraft, id: 'ac-2', registration: 'N12345' }}
          onOpenChange={vi.fn()}
          onSuccess={vi.fn()}
        />
      </SettingsProvider>,
    )
    await flush()

    // Back to "nothing chosen" - a confirm button still armed with the previous aircraft's
    // destination is a broken guard, not merely a stale one.
    expect(isDisabled(findButton(document.body, 'Select an airport'))).toBe(true)

    mounted.unmount()
  })
})
