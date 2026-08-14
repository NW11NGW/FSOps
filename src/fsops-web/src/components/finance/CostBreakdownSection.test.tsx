import { beforeEach, describe, expect, it, vi } from 'vitest'

import { CostBreakdownSection } from './CostBreakdownSection'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FinanceCosts } from '@/types/finance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function costs(overrides: Partial<FinanceCosts> = {}): FinanceCosts {
  return {
    periodDays: 30,
    fixed: {
      leasePayments: -40000,
      salaries: -18000,
      insurance: -4000,
      loanPayments: -12000,
      leaseEarlyTermination: 0,
      loanEarlySettlement: 0,
      total: -74000,
    },
    variable: {
      fuel: -31000,
      landingFees: -6200,
      handling: -3100,
      parkingFees: -900,
      passengerCharges: -2400,
      turnaroundFees: -1700,
      maintenance: -8000,
      crew: -5500,
      cancellationFees: 0,
      repositioning: 0,
      total: -58800,
    },
    revenue: { ticketRevenue: 190000, onlineFlyingBonus: 0, aircraftSaleProceeds: 0, total: 190000 },
    legacyDataNotice: null,
    ...overrides,
  }
}

async function render(status: 'loading' | 'ready' | 'error', data: FinanceCosts | null, periodDays = 30) {
  const mounted = await mount(
    <SettingsProvider>
      <CostBreakdownSection status={status} costs={data} periodDays={periodDays} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('CostBreakdownSection - states', () => {
  it('shows neither figures nor an error while loading', async () => {
    const { container, unmount } = await render('loading', null)

    expect(text(container)).not.toContain('Fixed costs')
    expect(text(container)).not.toContain('Could not load')

    unmount()
  })

  it('says the breakdown could not be loaded rather than rendering zeroes', async () => {
    const { container, unmount } = await render('error', null)

    expect(text(container)).toContain('Could not load your cost breakdown.')
    // Zeroes here would read as "you spent nothing", which is a different and false claim.
    expect(text(container)).not.toContain('$0.00')

    unmount()
  })

  it('states the window the figures cover, since a bare total means nothing without one', async () => {
    const { container, unmount } = await render('ready', costs({ periodDays: 90 }), 90)

    expect(text(container)).toContain('Last 90 days')

    unmount()
  })
})

describe('CostBreakdownSection - the fixed/variable/revenue split', () => {
  it('keeps fixed and variable costs in separate totals rather than one lump', async () => {
    const { container, unmount } = await render('ready', costs())

    const body = text(container)
    expect(body).toContain('Fixed costs')
    expect(body).toContain('Variable costs')
    // The split is the whole point of the card: fixed is owed whether or not you fly.
    expect(body).toContain('-$74,000.00')
    expect(body).toContain('-$58,800.00')
    expect(body).toContain('$190,000.00')

    unmount()
  })

  it('itemises every standing fixed cost, so none of them can go unaccounted for', async () => {
    const { container, unmount } = await render('ready', costs())

    const body = text(container)
    for (const label of ['Lease payments', 'Salaries', 'Insurance', 'Loan payments']) {
      expect(body).toContain(label)
    }

    unmount()
  })

  it('hides the occasional cost lines when they are zero, and shows them when they are not', async () => {
    const quiet = await render('ready', costs())
    expect(text(quiet.container)).not.toContain('Lease early termination')
    expect(text(quiet.container)).not.toContain('Cancellation fees')
    quiet.unmount()

    const eventful = await render(
      'ready',
      costs({
        fixed: { ...costs().fixed, leaseEarlyTermination: -9000 },
        variable: { ...costs().variable, cancellationFees: -1500 },
      }),
    )
    const body = text(eventful.container)
    expect(body).toContain('Lease early termination')
    expect(body).toContain('-$9,000.00')
    expect(body).toContain('Cancellation fees')
    expect(body).toContain('-$1,500.00')
    eventful.unmount()
  })

  it('shows aircraft sale proceeds separately from ticket revenue when a sale happened', async () => {
    const { container, unmount } = await render(
      'ready',
      costs({ revenue: { ticketRevenue: 190000, onlineFlyingBonus: 0, aircraftSaleProceeds: 24000000, total: 24190000 } }),
    )

    const body = text(container)
    // A one-off sale folded into ticket revenue would make the airline look far more profitable
    // than it trades.
    expect(body).toContain('Aircraft sale proceeds')
    expect(body).toContain('$24,000,000.00')
    expect(body).toContain('$190,000.00')

    unmount()
  })

  // The server counted the VATSIM online-flying uplift in NO total on this page until 2026-08-14,
  // while the cash balance (which just sums the ledger) counted it perfectly - so an online flyer's
  // Finances page disagreed with their own cash figure. It has its own line for the same reason it
  // has its own ledger category: it is earned by flying online, not by the fare.
  it('shows the online flying bonus as its own revenue line when there is one', async () => {
    const quiet = await render('ready', costs())
    expect(text(quiet.container)).not.toContain('Online flying bonus')
    quiet.unmount()

    const { container, unmount } = await render(
      'ready',
      costs({ revenue: { ticketRevenue: 190000, onlineFlyingBonus: 5700, aircraftSaleProceeds: 0, total: 195700 } }),
    )

    const body = text(container)
    expect(body).toContain('Online flying bonus')
    expect(body).toContain('$5,700.00')
    expect(body).toContain('$195,700.00')

    unmount()
  })

  // Repositioning has always been counted in the variable TOTAL but had no line of its own, so the
  // card's itemised lines could not add up to the number printed at the top of the same card.
  it('shows aircraft repositioning as its own variable line when there is one', async () => {
    const { container, unmount } = await render(
      'ready',
      costs({ variable: { ...costs().variable, repositioning: -2000, total: -60800 } }),
    )

    const body = text(container)
    expect(body).toContain('Aircraft repositioning')
    expect(body).toContain('-$2,000.00')

    unmount()
  })
})

describe('CostBreakdownSection - legacy data caveat', () => {
  it('shows the caveat verbatim when the window includes rows from before the split existed', async () => {
    const notice = 'Some older ledger lines predate the fixed/variable split and are counted as variable.'
    const { container, unmount } = await render('ready', costs({ legacyDataNotice: notice }))

    expect(text(container)).toContain(notice)

    unmount()
  })

  it('shows nothing when there is no caveat to give', async () => {
    const { container, unmount } = await render('ready', costs({ legacyDataNotice: null }))

    expect(text(container)).not.toContain('predate')

    unmount()
  })
})
