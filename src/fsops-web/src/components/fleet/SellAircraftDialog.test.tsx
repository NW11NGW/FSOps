import { beforeEach, describe, expect, it, vi } from 'vitest'

import { SellAircraftDialog } from './SellAircraftDialog'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'
import type { FleetAircraftSummary, SaleQuote } from '@/types/fleet'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { ApiError, get, post } from '@/lib/api'

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
  vi.mocked(get).mockReset()
  mockGetDefault()
  vi.mocked(post).mockReset()
})

function quoteFetchCount(): number {
  return vi.mocked(get).mock.calls.filter(([path]) => String(path).includes('/sale-quote')).length
}

async function mountConfirmed(onSuccess = vi.fn(), onOpenChange = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <SellAircraftDialog aircraft={aircraft} onOpenChange={onOpenChange} onSuccess={onSuccess} />
    </SettingsProvider>,
  )
  await flush()

  const confirmInput = document.body.querySelector<HTMLInputElement>('#sell-confirm')
  if (!confirmInput) throw new Error('sell-confirm input not found')
  typeInto(confirmInput, 'g-abcd')

  return mounted
}

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

/**
 * The same discrimination EndLeaseDialog and LoanRepaymentDialog carry. A sale quote is priced off
 * discrete state (airframe hours, condition) rather than the clock, so this dialog never swallowed
 * clicks the way the end-lease one did - but it lumped every refusal into "the figures changed",
 * which is misleading regardless of whether it also costs the player a click.
 */
describe('SellAircraftDialog - a refusal must read as itself', () => {
  it('reports a hard refusal as itself and does NOT re-quote', async () => {
    const message = 'G-ABCD is on Jo Bloggs’ standing schedule (4 leg(s)/week) - remove it from their schedule first.'
    vi.mocked(post).mockRejectedValue(new ApiError(400, message, { error: message }))

    const mounted = await mountConfirmed()
    const quotesBefore = quoteFetchCount()

    click(findButton(document.body, 'Sell for'))
    await flush()

    const dialogText = document.body.textContent ?? ''
    expect(dialogText).toContain('standing schedule')
    expect(dialogText).not.toContain("figures changed")
    expect(quoteFetchCount()).toBe(quotesBefore)

    mounted.unmount()
  })

  it('treats a genuine stale-quote refusal as a re-quote, and says what changed', async () => {
    const message = 'The sale value has changed since you last checked (was 24000000.00, now 23880000.00) - please confirm the new figure.'
    vi.mocked(post).mockRejectedValue(new ApiError(400, message, { error: message, currentSaleValue: 23880000 }))

    const mounted = await mountConfirmed()
    const quotesBefore = quoteFetchCount()

    mockGetDefault({ ...quote, saleValue: 23880000, airframeHours: 12400 })

    click(findButton(document.body, 'Sell for'))
    await flush()

    expect(quoteFetchCount()).toBe(quotesBefore + 1)
    expect(document.body.textContent ?? '').toContain("figures changed")

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
