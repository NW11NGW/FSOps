import { beforeEach, describe, expect, it, vi } from 'vitest'

import { LedgerSection } from './LedgerSection'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, flush, getByRole, isDisabled, mount, queryAllByRole, queryByRole, selectOption, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { LedgerTransactionEntry } from '@/types/finance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function line(overrides: Partial<LedgerTransactionEntry> = {}): LedgerTransactionEntry {
  return {
    id: 'tx-1',
    utc: '2026-08-11T14:32:00Z',
    category: 'TicketRevenue',
    amount: 18400,
    description: 'Ticket revenue EGGD-EGPH',
    flightId: 'flight-1',
    ...overrides,
  }
}

/** Answers the settings GETs, then the ledger GET however the test wants it to behave. */
function stubLedger(respond: () => Promise<unknown>) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    const settings = settingsResponseFor(path)
    if (settings) return settings as never
    if (path === '/finance/ledger') return (await respond()) as never
    throw new Error(`unexpected GET ${path}`)
  })
}

function stubLedgerPage(transactions: LedgerTransactionEntry[], total = transactions.length) {
  stubLedger(async () => ({ total, transactions }))
}

async function render() {
  const mounted = await mount(
    <SettingsProvider>
      <LedgerSection />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockReset()
})

describe('LedgerSection - the three states before any rows exist', () => {
  it('shows a loading placeholder rather than an empty ledger while the fetch is still in flight', async () => {
    // Never resolves: the point is what the user sees DURING the request, which is precisely when
    // an "empty" message would be a lie.
    stubLedger(() => new Promise(() => {}))

    const { container, unmount } = await mount(
      <SettingsProvider>
        <LedgerSection />
      </SettingsProvider>,
    )

    expect(text(container)).not.toContain('Nothing posted here yet')
    expect(queryAllByRole(container, 'row')).toHaveLength(0)

    unmount()
  })

  it('says the ledger could not be loaded when the request fails, instead of showing it as empty', async () => {
    stubLedger(() => Promise.reject(new Error('offline')))

    const { container, unmount } = await render()

    // The distinction that matters: "we could not load this" and "you have not traded yet" are
    // completely different facts, and showing the second when the first is true is the failure.
    expect(text(container)).toContain('Could not load the ledger.')
    expect(text(container)).not.toContain('Nothing posted here yet')

    unmount()
  })

  it('explains an empty ledger in terms of the airline, not the request', async () => {
    stubLedgerPage([])

    const { container, unmount } = await render()

    expect(text(container)).toContain('Nothing posted here yet')
    expect(text(container)).toContain('Ledger lines appear as your airline trades.')

    unmount()
  })

  it('blames the filter, not the airline, when a category filter is what emptied the list', async () => {
    stubLedgerPage([])
    const { container, unmount } = await render()

    const filter = getByRole(container, 'combobox', { name: 'Filter by category' }) as HTMLSelectElement
    selectOption(filter, 'Fuel')
    await flush()

    expect(text(container)).toContain('No fuel lines yet.')

    unmount()
  })
})

describe('LedgerSection - rendering posted lines', () => {
  it('shows each line\'s description, human-readable category and signed amount', async () => {
    stubLedgerPage([
      line({ id: 'tx-1', category: 'TicketRevenue', amount: 18400, description: 'Ticket revenue EGGD-EGPH' }),
      line({ id: 'tx-2', category: 'LandingFees', amount: -1250, description: 'Landing fee EGPH' }),
    ])

    const { container, unmount } = await render()

    const body = text(container)
    expect(body).toContain('Ticket revenue EGGD-EGPH')
    expect(body).toContain('Landing fee EGPH')
    // The raw enum name leaking through would be a real regression - the label map is the thing
    // that turns "LandingFees" into something a person reads.
    expect(body).toContain('Landing fees')
    expect(body).not.toContain('LandingFees')
    // A cost must read as a cost. An unsigned "$1,250.00" next to revenue is the one formatting
    // slip on this screen that would actively mislead.
    expect(body).toContain('$18,400.00')
    expect(body).toContain('-$1,250.00')

    unmount()
  })

  it('offers the flight drill-down only on lines that came from a flight', async () => {
    stubLedgerPage([
      line({ id: 'tx-1', description: 'Ticket revenue EGGD-EGPH', flightId: 'flight-1' }),
      line({ id: 'tx-2', category: 'Insurance', amount: -4000, description: 'Monthly insurance', flightId: null }),
    ])

    const { container, unmount } = await render()

    // One button, not two: an insurance charge has no flight behind it, and a drill-down that
    // opened an empty dialog would be worse than no drill-down at all.
    expect(queryAllByRole(container, 'button', { name: 'View the flight this line came from' })).toHaveLength(1)

    unmount()
  })
})

describe('LedgerSection - pagination', () => {
  it('reports which slice of the ledger is on screen', async () => {
    stubLedgerPage(Array.from({ length: 25 }, (_, i) => line({ id: `tx-${i}` })), 60)

    const { container, unmount } = await render()

    expect(text(container)).toContain('1-25 of 60')

    unmount()
  })

  it('disables "Previous page" on the first page', async () => {
    stubLedgerPage(Array.from({ length: 25 }, (_, i) => line({ id: `tx-${i}` })), 60)

    const { container, unmount } = await render()

    expect(isDisabled(getByRole(container, 'button', { name: 'Previous page' }))).toBe(true)

    unmount()
  })

  it('moves to the next slice and re-enables going back', async () => {
    stubLedgerPage(Array.from({ length: 25 }, (_, i) => line({ id: `tx-${i}` })), 60)

    const { container, unmount } = await render()

    click(getByRole(container, 'button', { name: 'Next page' }))
    await flush()

    expect(text(container)).toContain('26-50 of 60')
    expect(isDisabled(getByRole(container, 'button', { name: 'Previous page' }))).toBe(false)

    unmount()
  })

  it('caps the range at the total on a partial last page, rather than promising rows that are not there', async () => {
    stubLedgerPage(Array.from({ length: 25 }, (_, i) => line({ id: `tx-${i}` })), 60)

    const { container, unmount } = await render()

    click(getByRole(container, 'button', { name: 'Next page' }))
    await flush()
    click(getByRole(container, 'button', { name: 'Next page' }))
    await flush()

    // 60 rows in pages of 25 leaves 10 on the last page - "51-75 of 60" would be nonsense.
    expect(text(container)).toContain('51-60 of 60')

    // Genuinely disabled, not merely styled to look it. This is the case that can tell the
    // difference: a handler that still fired here would page past the end of the ledger and
    // render "76-60 of 60". (The same click on "Previous page" at the first page would be
    // indistinguishable, because its handler clamps to zero and changes nothing either way.)
    const next = getByRole(container, 'button', { name: 'Next page' })
    expect(isDisabled(next)).toBe(true)
    click(next)
    await flush()
    expect(text(container)).toContain('51-60 of 60')

    unmount()
  })

  it('disables both directions when everything already fits on one page', async () => {
    stubLedgerPage(Array.from({ length: 4 }, (_, i) => line({ id: `tx-${i}` })), 4)

    const { container, unmount } = await render()

    expect(text(container)).toContain('1-4 of 4')
    expect(isDisabled(getByRole(container, 'button', { name: 'Previous page' }))).toBe(true)
    expect(isDisabled(getByRole(container, 'button', { name: 'Next page' }))).toBe(true)

    unmount()
  })

  it('shows no pagination controls at all when the ledger is empty', async () => {
    stubLedgerPage([])

    const { container, unmount } = await render()

    expect(queryByRole(container, 'button', { name: 'Next page' })).toBeNull()
    expect(queryByRole(container, 'button', { name: 'Previous page' })).toBeNull()

    unmount()
  })
})
