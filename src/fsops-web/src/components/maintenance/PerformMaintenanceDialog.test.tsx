import { beforeEach, describe, expect, it, vi } from 'vitest'

import { PerformMaintenanceDialog } from './PerformMaintenanceDialog'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, getByRole, isDisabled, mount, queryByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { MaintenanceQuoteResponse } from '@/types/maintenance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

import { get, post } from '@/lib/api'

function quote(overrides: Partial<MaintenanceQuoteResponse> = {}): MaintenanceQuoteResponse {
  return {
    fleetAircraftId: 'ac-1',
    registration: 'G-ABCD',
    currentStatus: 'Active',
    canPerform: true,
    blockReason: null,
    affectedSchedules: [],
    quotes: [
      { type: 'ACheck', cost: 42000, downtimeHours: 12, hoursSinceCheck: 320, hoursRemainingUntilNatural: 180 },
      { type: 'CCheck', cost: 610000, downtimeHours: 168, hoursSinceCheck: 1200, hoursRemainingUntilNatural: 2800 },
    ],
    ...overrides,
  }
}

function stubQuote(respond: () => Promise<unknown>) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    const settings = settingsResponseFor(path)
    if (settings) return settings as never
    if (path.includes('/maintenance-quote')) return (await respond()) as never
    throw new Error(`unexpected GET ${path}`)
  })
}

/**
 * The dialog's confirm button, whatever it currently calls itself.
 *
 * It is labelled "Ground now - <cost>" only when the quote is actually performable; every other
 * state - still loading, failed to load, or blocked outright - falls back to "Perform now" with no
 * figure attached. A price on an action the player cannot take reads as "this is what it will cost
 * you" even though nothing is at risk, so the label withholds the number whenever the button is
 * disabled for that reason. Matching on either label keeps that presentational choice out of the
 * assertions that are actually about whether the control can be used.
 */
function confirmButton(): HTMLElement {
  return getByRole(document.body, 'button', { name: /^(Ground now|Perform now)/ })
}

async function render(onOpenChange = vi.fn(), onSuccess = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <PerformMaintenanceDialog fleetAircraftId="ac-1" onOpenChange={onOpenChange} onSuccess={onSuccess} />
    </SettingsProvider>,
  )
  await flush()
  // Dialog content portals into document.body, not the mount container.
  return { ...mounted, onOpenChange, onSuccess }
}

beforeEach(() => {
  vi.mocked(post).mockReset()
})

describe('PerformMaintenanceDialog - before a quote exists', () => {
  it('offers no way to ground the aircraft while the quote is still loading', async () => {
    stubQuote(() => new Promise(() => {}))

    const mounted = await mount(
      <SettingsProvider>
        <PerformMaintenanceDialog fleetAircraftId="ac-1" onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )

    // Confirming against a cost nobody has been shown is the exact failure this guard prevents.
    expect(isDisabled(confirmButton())).toBe(true)

    mounted.unmount()
  })

  it('says the quote could not be loaded, and still refuses to commit', async () => {
    stubQuote(() => Promise.reject(new Error('offline')))

    const { unmount } = await render()

    expect(text(document.body)).toContain("Could not load this aircraft's maintenance status.")
    expect(isDisabled(confirmButton())).toBe(true)

    unmount()
  })
})

describe('PerformMaintenanceDialog - when maintenance is blocked', () => {
  it('shows the server\'s own reason rather than a generic refusal', async () => {
    stubQuote(async () => quote({ canPerform: false, blockReason: 'G-ABCD is airborne on FSO204.' }))

    const { unmount } = await render()

    expect(text(document.body)).toContain('G-ABCD is airborne on FSO204.')

    unmount()
  })

  it('disables the confirm button, so a blocked aircraft cannot be grounded anyway', async () => {
    stubQuote(async () => quote({ canPerform: false, blockReason: 'G-ABCD is already in maintenance.' }))

    const { unmount } = await render()

    expect(isDisabled(confirmButton())).toBe(true)

    unmount()
  })

  it('does not price an action the player cannot take', async () => {
    stubQuote(async () => quote({ canPerform: false, blockReason: 'G-ABCD is airborne on FSO204.' }))

    const { unmount } = await render()

    // A quote exists (the A-check quote is right there in the stub), but the button must not
    // show its cost while the action itself is blocked - that would read as a price on something
    // the player could actually do.
    expect(queryByRole(document.body, 'button', { name: /^Ground now/ })).toBeNull()
    expect(getByRole(document.body, 'button', { name: 'Perform now' })).toBeTruthy()

    unmount()
  })

  it('shows the block reason instead of the check details a player could act on', async () => {
    stubQuote(async () => quote({ canPerform: false, blockReason: 'G-ABCD is airborne.' }))

    const { unmount } = await render()

    const body = text(document.body)
    expect(body).toContain('G-ABCD is airborne.')
    // The quote panel itself is withheld: no tabs to pick a check, no downtime, no forfeiture
    // warning.
    expect(body).not.toContain('Hours since last check')
    expect(body).not.toContain('are forfeited, not refunded')

    unmount()
  })
})

describe('PerformMaintenanceDialog - stating the trade-off', () => {
  it('names the aircraft', async () => {
    stubQuote(async () => quote())

    const { unmount } = await render()

    expect(text(document.body)).toContain('G-ABCD')

    unmount()
  })

  it('shows the full cost and downtime, and says the remaining hours are forfeited', async () => {
    stubQuote(async () => quote())

    const { unmount } = await render()

    const body = text(document.body)
    expect(body).toContain('$42,000.00')
    expect(body).toContain('12h')
    expect(body).toContain('180h')
    // The whole reason this dialog exists: bringing a check forward is never a discount, and the
    // player must not be able to believe it is.
    expect(body).toContain('are forfeited, not refunded')
    expect(body).toContain('never cheaper than letting it fall due')

    unmount()
  })

  it('puts the cost on the confirm button itself, not only in the body', async () => {
    stubQuote(async () => quote())

    const { unmount } = await render()

    // The last thing under the cursor should state what it charges.
    expect(getByRole(document.body, 'button', { name: 'Ground now - $42,000.00' })).toBeTruthy()

    unmount()
  })

  it('switches every figure over when the C-check is chosen instead', async () => {
    stubQuote(async () => quote())

    const { unmount } = await render()

    click(getByRole(document.body, 'tab', { name: 'C-check' }))
    await flush()

    const body = text(document.body)
    expect(body).toContain('$610,000.00')
    expect(body).toContain('168h')
    expect(body).not.toContain('$42,000.00')
    expect(getByRole(document.body, 'button', { name: 'Ground now - $610,000.00' })).toBeTruthy()

    unmount()
  })

  it('names whose schedules the grounding will interrupt', async () => {
    stubQuote(async () =>
      quote({
        affectedSchedules: [
          { pilotId: 'p1', pilotName: 'A. Okafor', dayOfWeek: 'Tuesday', departureTimeUtc: '08:15', routeLabel: 'EGGD-EGPH' },
          { pilotId: 'p2', pilotName: 'R. Lindqvist', dayOfWeek: 'Thursday', departureTimeUtc: '14:40', routeLabel: 'EGPH-EGGD' },
        ],
      }),
    )

    const { unmount } = await render()

    const body = text(document.body)
    expect(body).toContain('Affects 2 scheduled leg(s)')
    expect(body).toContain('A. Okafor')
    expect(body).toContain('R. Lindqvist')

    unmount()
  })

  it('says nothing about schedules when no leg is affected', async () => {
    stubQuote(async () => quote({ affectedSchedules: [] }))

    const { unmount } = await render()

    expect(text(document.body)).not.toContain('scheduled leg(s)')

    unmount()
  })
})

describe('PerformMaintenanceDialog - committing', () => {
  it('sends the exact check, cost and downtime the player was shown, not an empty body', async () => {
    stubQuote(async () => quote())
    vi.mocked(post).mockResolvedValue({
      fleetAircraftId: 'ac-1',
      registration: 'G-ABCD',
      type: 'ACheck',
      groundedUntilUtc: '2026-08-12T02:00:00Z',
      cost: 42000,
      cashBalance: 900000,
    })

    const { onSuccess, unmount } = await render()

    click(getByRole(document.body, 'button', { name: 'Ground now - $42,000.00' }))
    await flush()

    // The server re-quotes and compares: sending the wrong figure, or none, is what turns a
    // stale-quote guard into a silent overcharge.
    expect(post).toHaveBeenCalledWith('/fleet/ac-1/perform-maintenance', {
      type: 'ACheck',
      expectedCost: 42000,
      expectedDowntimeHours: 12,
    })
    expect(onSuccess).toHaveBeenCalled()

    unmount()
  })

  it('sends the C-check figures after the player switches to it', async () => {
    stubQuote(async () => quote())
    vi.mocked(post).mockResolvedValue({
      fleetAircraftId: 'ac-1',
      registration: 'G-ABCD',
      type: 'CCheck',
      groundedUntilUtc: '2026-08-19T02:00:00Z',
      cost: 610000,
      cashBalance: 400000,
    })

    const { unmount } = await render()

    click(getByRole(document.body, 'tab', { name: 'C-check' }))
    await flush()
    click(getByRole(document.body, 'button', { name: 'Ground now - $610,000.00' }))
    await flush()

    expect(post).toHaveBeenCalledWith('/fleet/ac-1/perform-maintenance', {
      type: 'CCheck',
      expectedCost: 610000,
      expectedDowntimeHours: 168,
    })

    unmount()
  })

  it('surfaces a refusal instead of closing as though it had worked', async () => {
    stubQuote(async () => quote())
    const { ApiError } = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
    vi.mocked(post).mockRejectedValue(new ApiError(400, 'The quote has changed since it was shown.'))

    const { onOpenChange, onSuccess, unmount } = await render()

    click(getByRole(document.body, 'button', { name: 'Ground now - $42,000.00' }))
    await flush()

    expect(text(document.body)).toContain('The quote has changed since it was shown.')
    expect(onSuccess).not.toHaveBeenCalled()
    expect(onOpenChange).not.toHaveBeenCalledWith(false)

    unmount()
  })

  it('renders nothing at all when no aircraft is selected', async () => {
    stubQuote(async () => quote())

    const mounted = await mount(
      <SettingsProvider>
        <PerformMaintenanceDialog fleetAircraftId={null} onOpenChange={vi.fn()} onSuccess={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(queryByRole(document.body, 'button', { name: /^(Ground now|Perform now)/ })).toBeNull()
    expect(text(document.body)).not.toContain('Perform maintenance now')

    mounted.unmount()
  })
})
