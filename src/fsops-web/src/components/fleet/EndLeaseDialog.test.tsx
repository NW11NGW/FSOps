import { beforeEach, describe, expect, it, vi } from 'vitest'

import { EndLeaseDialog } from './EndLeaseDialog'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'
import type { LeaseTerminationQuote } from '@/types/fleet'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { ApiError, get, post } from '@/lib/api'

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
        timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path.includes('/lease-termination-quote')) return quoteOverride as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

beforeEach(() => {
  vi.mocked(get).mockReset()
  mockGetDefault()
  vi.mocked(post).mockReset()
})

/** Mounts the dialog and gets as far as a valid, confirmed submit - the state every test below
 *  starts from. Returns the mounted tree so the caller can unmount it. */
async function mountConfirmed(onSuccess = vi.fn(), onOpenChange = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <EndLeaseDialog target={target} onOpenChange={onOpenChange} onSuccess={onSuccess} />
    </SettingsProvider>,
  )
  await flush()

  const confirmInput = document.body.querySelector<HTMLInputElement>('#end-lease-confirm')
  if (!confirmInput) throw new Error('end-lease-confirm input not found')
  typeInto(confirmInput, 'g-abcd')

  return mounted
}

function quoteFetchCount(): number {
  return vi.mocked(get).mock.calls.filter(([path]) => String(path).includes('/lease-termination-quote')).length
}

describe('EndLeaseDialog - request body', () => {
  it('sends the confirmed total charge AND the billing period it was priced in', async () => {
    vi.mocked(post).mockResolvedValue({ fleetAircraftId: 'ac-1', proRataAmount: 100000, earlyTerminationFee: 50000, totalCharge: 150000, cashBalance: 100000 })

    const onSuccess = vi.fn()
    const mounted = await mountConfirmed(onSuccess)

    click(findButton(document.body, 'End lease -'))
    await flush()

    expect(post).toHaveBeenCalledWith('/fleet/ac-1/end-lease', {
      expectedTotalCharge: 150000,
      expectedPeriodStartUtc: '2026-08-01T00:00:00Z',
    })
    expect(onSuccess).toHaveBeenCalled()

    mounted.unmount()
  })
})

/**
 * The regression tests for "when ending a lease you have to click nearly 3 times".
 *
 * The server used to compare the confirmed charge exactly, against a figure that grows with the
 * clock - so every click was refused and re-quoted, and the re-quote banner that appeared was
 * indistinguishable from a button that had done nothing. The fix is server-side (the commit now
 * guards on the billing period), but the shape of the assertion belongs here: a destructive,
 * money-spending action must complete on ONE deliberate confirmation, and a click that does not
 * complete must say why.
 */
describe('EndLeaseDialog - one click is one click', () => {
  it('ends the lease on the FIRST click - one POST, no silent re-quote loop', async () => {
    vi.mocked(post).mockResolvedValue({ fleetAircraftId: 'ac-1', proRataAmount: 100000, earlyTerminationFee: 50000, totalCharge: 150000, cashBalance: 100000 })

    const onSuccess = vi.fn()
    const onOpenChange = vi.fn()
    const mounted = await mountConfirmed(onSuccess, onOpenChange)
    const quotesBefore = quoteFetchCount()

    click(findButton(document.body, 'End lease -'))
    await flush()

    expect(post).toHaveBeenCalledTimes(1)
    expect(onSuccess).toHaveBeenCalledTimes(1)
    expect(onOpenChange).toHaveBeenCalledWith(false)
    // No re-quote: a successful commit must not send the player round the loop again.
    expect(quoteFetchCount()).toBe(quotesBefore)

    mounted.unmount()
  })

  it('reports a hard refusal as itself and does NOT re-quote - an insufficient-funds message must never read as "the figures changed"', async () => {
    // Exactly what the server sends when there is not enough cash: a plain `error`, and crucially
    // NO `currentTotalCharge`, which is what distinguishes it from a genuine stale quote.
    const message = 'Ending this lease now would cost 150000.00 (pro-rata rent plus early-termination fee), you have 900.00.'
    vi.mocked(post).mockRejectedValue(new ApiError(400, message, { error: message }))

    const mounted = await mountConfirmed()
    const quotesBefore = quoteFetchCount()

    click(findButton(document.body, 'End lease -'))
    await flush()

    const dialogText = document.body.textContent ?? ''
    expect(dialogText).toContain('you have 900.00')
    expect(dialogText).not.toContain('new billing period')
    // The quote was never wrong, so it must be left alone rather than re-fetched.
    expect(quoteFetchCount()).toBe(quotesBefore)

    mounted.unmount()
  })

  it('treats a genuine stale-quote refusal as a re-quote, and says what changed', async () => {
    const message = 'A rent payment fell due while you were deciding, so this lease has moved into a new billing period.'
    vi.mocked(post).mockRejectedValue(new ApiError(400, message, { error: message, currentTotalCharge: 51000 }))

    const mounted = await mountConfirmed()
    const quotesBefore = quoteFetchCount()

    // The re-quote that follows returns the post-billing-tick figures.
    mockGetDefault({ ...quote, daysIntoCurrentPeriod: 0.2, proRataAmount: 2000, totalCharge: 52000 })

    click(findButton(document.body, 'End lease -'))
    await flush()

    expect(quoteFetchCount()).toBe(quotesBefore + 1)
    const dialogText = document.body.textContent ?? ''
    expect(dialogText).toContain('new billing period')
    expect(dialogText).toContain('Nothing has been charged')

    mounted.unmount()
  })

  it('still says something when a stale-quote refusal re-quotes to an unchanged figure - a click must never look like it did nothing', async () => {
    const message = 'A rent payment fell due while you were deciding, so this lease has moved into a new billing period.'
    vi.mocked(post).mockRejectedValue(new ApiError(400, message, { error: message, currentTotalCharge: 150000 }))

    const mounted = await mountConfirmed()

    // Deliberately the SAME quote back, so the "figures changed" banner has nothing to report.
    click(findButton(document.body, 'End lease -'))
    await flush()

    expect(document.body.textContent ?? '').toContain('new billing period')

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
