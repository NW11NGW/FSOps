import { beforeEach, describe, expect, it, vi } from 'vitest'

import { SellAircraftDialog } from './SellAircraftDialog'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'
import type { FleetAircraftSummary, SaleQuote } from '@/types/fleet'

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
  locationIcao: 'EGLL',
  airframeHours: 12000,
  hoursSinceACheck: 100,
  hoursSinceCCheck: 500,
  hoursToNextACheck: 400,
  hoursToNextCCheck: 4500,
  conditionPercent: 82,
  fuelOnBoardKg: 5000,
  groundedUntilUtc: null,
  groundedReason: null,
  reservedForPlayer: false,
  createdUtc: '2026-01-01T00:00:00Z',
}

const quote: SaleQuote = {
  fleetAircraftId: 'ac-1',
  registration: 'G-ABCD',
  aircraftTypeName: 'A320-200',
  conditionPercent: 82,
  airframeHours: 12000,
  newPrice: 40000000,
  resaleFactorApplied: 0.6,
  saleValue: 24000000,
  isGroundedForMaintenance: false,
  isLastAircraft: false,
  standingScheduleConflict: null,
  canSell: true,
  blockReason: null,
  loanNote: 'Selling never pays off an outstanding loan.',
}

function mockGetDefault(quoteOverride: SaleQuote = quote) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return {
        currencyCode: 'USD', distanceUnit: 'Nm', altitudeUnit: 'Feet', weightUnit: 'Kg',
        timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path.includes('/sale-quote')) return quoteOverride as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

beforeEach(() => {
  mockGetDefault()
  vi.mocked(post).mockReset()
})

describe('SellAircraftDialog - request body', () => {
  it('sends the confirmed sale value the player was shown, not an empty body', async () => {
    vi.mocked(post).mockResolvedValue({ fleetAircraftId: 'ac-1', saleValue: 24000000, cashBalance: 100000 })

    const onOpenChange = vi.fn()
    const onSuccess = vi.fn()
    const mounted = await mount(
      <SettingsProvider>
        <SellAircraftDialog aircraft={aircraft} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#sell-confirm')
    if (!confirmInput) throw new Error('sell-confirm input not found')
    typeInto(confirmInput, 'g-abcd') // lower case - the dialog upper-cases as you type

    const sellButton = findButton(document.body, 'Sell for')
    click(sellButton)
    await flush()

    expect(post).toHaveBeenCalledWith('/fleet/ac-1/sell', { expectedSaleValue: 24000000 })
    expect(onSuccess).toHaveBeenCalled()

    mounted.unmount()
  })

  it('never enables the sell button before the registration is typed correctly', async () => {
    const mounted = await mount(
      <SettingsProvider>
        <SellAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const sellButton = findButton(document.body, 'Sell for')
    expect(sellButton.hasAttribute('disabled')).toBe(true)

    mounted.unmount()
  })
})

describe('SellAircraftDialog - reset behaviour', () => {
  it('does NOT wipe the confirm text when the parent re-renders with the same aircraft id', async () => {
    const mounted = await mount(
      <SettingsProvider>
        <SellAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#sell-confirm')
    if (!confirmInput) throw new Error('sell-confirm input not found')
    typeInto(confirmInput, 'G-AB')

    // Fresh object literal, same aircraft - simulates a page re-rendering on a live heartbeat.
    await mounted.rerender(
      <SettingsProvider>
        <SellAircraftDialog aircraft={{ ...aircraft }} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(document.body.querySelector<HTMLInputElement>('#sell-confirm')?.value).toBe('G-AB')

    mounted.unmount()
  })

  it('DOES clear stale confirm text when it opens for a genuinely different aircraft - a confirmation field carrying text typed for a PREVIOUS aircraft is a broken guard, not just a wiped one', async () => {
    const mounted = await mount(
      <SettingsProvider>
        <SellAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#sell-confirm')
    if (!confirmInput) throw new Error('sell-confirm input not found')
    typeInto(confirmInput, 'G-AB')

    const otherAircraft: FleetAircraftSummary = { ...aircraft, id: 'ac-2', registration: 'N12345' }
    mockGetDefault({ ...quote, fleetAircraftId: 'ac-2', registration: 'N12345' })
    await mounted.rerender(
      <SettingsProvider>
        <SellAircraftDialog aircraft={otherAircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(document.body.querySelector<HTMLInputElement>('#sell-confirm')?.value).toBe('')

    mounted.unmount()
  })
})
