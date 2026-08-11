import { beforeEach, describe, expect, it, vi } from 'vitest'

import { EndLeaseDialog } from './EndLeaseDialog'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'
import type { LeaseTerminationQuote } from '@/types/fleet'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post } from '@/lib/api'

const target = { id: 'ac-1', registration: 'G-ABCD' }

const quote: LeaseTerminationQuote = {
  fleetAircraftId: 'ac-1',
  leaseId: 'lease-1',
  registration: 'G-ABCD',
  aircraftTypeName: 'A320-200',
  monthlyRate: 300000,
  currentPeriodStartUtc: '2026-08-01T00:00:00Z',
  nextScheduledPaymentUtc: '2026-08-31T00:00:00Z',
  daysIntoCurrentPeriod: 10,
  proRataAmount: 100000,
  earlyTerminationFee: 50000,
  totalCharge: 150000,
  isLastAircraft: false,
  standingScheduleConflict: null,
  canEndLease: true,
  blockReason: null,
}

function mockGetDefault(quoteOverride: LeaseTerminationQuote = quote) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return {
        currencyCode: 'USD', distanceUnit: 'Nm', altitudeUnit: 'Feet', weightUnit: 'Kg',
        timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', communityFolderPath: null, simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path.includes('/lease-termination-quote')) return quoteOverride as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

beforeEach(() => {
  mockGetDefault()
  vi.mocked(post).mockReset()
})

describe('EndLeaseDialog - request body', () => {
  it('sends the confirmed total charge the player was shown, not an empty body', async () => {
    vi.mocked(post).mockResolvedValue({ fleetAircraftId: 'ac-1', proRataAmount: 100000, earlyTerminationFee: 50000, totalCharge: 150000, cashBalance: 100000 })

    const onSuccess = vi.fn()
    const mounted = await mount(
      <SettingsProvider>
        <EndLeaseDialog target={target} onOpenChange={vi.fn()} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#end-lease-confirm')
    if (!confirmInput) throw new Error('end-lease-confirm input not found')
    typeInto(confirmInput, 'g-abcd')

    click(findButton(document.body, 'End lease -'))
    await flush()

    expect(post).toHaveBeenCalledWith('/fleet/ac-1/end-lease', { expectedTotalCharge: 150000 })
    expect(onSuccess).toHaveBeenCalled()

    mounted.unmount()
  })
})

describe('EndLeaseDialog - reset behaviour', () => {
  it('does NOT wipe the confirm text when the parent re-renders with the same target id', async () => {
    const mounted = await mount(
      <SettingsProvider>
        <EndLeaseDialog target={target} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#end-lease-confirm')
    if (!confirmInput) throw new Error('end-lease-confirm input not found')
    typeInto(confirmInput, 'G-AB')

    // Fresh object literal, same id - simulates the parent re-rendering on a live heartbeat
    // (both Fleet.tsx and Finances.tsx build `target` inline).
    await mounted.rerender(
      <SettingsProvider>
        <EndLeaseDialog target={{ ...target }} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(document.body.querySelector<HTMLInputElement>('#end-lease-confirm')?.value).toBe('G-AB')

    mounted.unmount()
  })

  it('DOES clear stale confirm text when it opens for a genuinely different target - a confirmation field carrying text typed for a PREVIOUS aircraft is a broken guard, not just a wiped one', async () => {
    const mounted = await mount(
      <SettingsProvider>
        <EndLeaseDialog target={target} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const confirmInput = document.body.querySelector<HTMLInputElement>('#end-lease-confirm')
    if (!confirmInput) throw new Error('end-lease-confirm input not found')
    typeInto(confirmInput, 'G-AB')

    const otherTarget = { id: 'ac-2', registration: 'N12345' }
    mockGetDefault({ ...quote, fleetAircraftId: 'ac-2', registration: 'N12345' })
    await mounted.rerender(
      <SettingsProvider>
        <EndLeaseDialog target={otherTarget} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(document.body.querySelector<HTMLInputElement>('#end-lease-confirm')?.value).toBe('')

    mounted.unmount()
  })
})
