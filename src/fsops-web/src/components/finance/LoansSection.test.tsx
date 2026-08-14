import { beforeEach, describe, expect, it, vi } from 'vitest'

import { LoansSection } from './LoansSection'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, getByRole, mount, queryAllByRole, queryByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FinanceLoan } from '@/types/finance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function loan(overrides: Partial<FinanceLoan> = {}): FinanceLoan {
  return {
    loanId: 'loan-1',
    principal: 5000000,
    remainingBalance: 3200000,
    annualInterestRate: 6.5,
    monthlyPayment: 96000,
    startUtc: '2026-01-15T00:00:00Z',
    termMonths: 60,
    remainingTermMonths: 41,
    totalInterestRemaining: 736000,
    isPaidOff: false,
    ...overrides,
  }
}

async function render(status: 'loading' | 'ready' | 'error', loans: FinanceLoan[], onRepay = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <LoansSection status={status} loans={loans} onRepay={onRepay} />
    </SettingsProvider>,
  )
  await flush()
  return { ...mounted, onRepay }
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('LoansSection - states', () => {
  it('shows no loan rows while loading', async () => {
    const { container, unmount } = await render('loading', [])

    expect(queryAllByRole(container, 'row')).toHaveLength(0)
    expect(text(container)).not.toContain('No loans')

    unmount()
  })

  it('distinguishes "could not load" from "you have no loans"', async () => {
    const { container, unmount } = await render('error', [])

    expect(text(container)).toContain('Could not load your loans.')
    // Telling a player with debt that they have none is the worst possible confusion here.
    expect(text(container)).not.toContain('No loans')

    unmount()
  })

  it('points somewhere useful when there are genuinely no loans', async () => {
    const { container, unmount } = await render('ready', [])

    expect(text(container)).toContain('No loans')
    expect(text(container)).toContain('Take out a loan from the Fleet page')

    unmount()
  })
})

describe('LoansSection - an active loan', () => {
  it('shows the original amount and the outstanding balance as separate figures', async () => {
    const { container, unmount } = await render('ready', [loan()])

    const body = text(container)
    // Showing only one of these is the classic loan-display bug: the player cannot tell how much
    // they borrowed from how much they still owe.
    expect(body).toContain('$5,000,000.00')
    expect(body).toContain('$3,200,000.00')

    unmount()
  })

  it('shows the rate, the monthly payment, the term progress and the interest still to pay', async () => {
    const { container, unmount } = await render('ready', [loan()])

    const body = text(container)
    expect(body).toContain('6.50%')
    expect(body).toContain('$96,000.00')
    expect(body).toContain('41 of 60 mo')
    expect(body).toContain('$736,000.00')

    unmount()
  })

  it('offers a repay action and hands the whole loan back to the caller', async () => {
    const active = loan()
    const { container, onRepay, unmount } = await render('ready', [active])

    click(getByRole(container, 'button', { name: 'Repay' }))

    expect(onRepay).toHaveBeenCalledWith(active)

    unmount()
  })
})

describe('LoansSection - a paid-off loan', () => {
  const settled = loan({
    loanId: 'loan-2',
    remainingBalance: 0,
    remainingTermMonths: 0,
    totalInterestRemaining: 0,
    isPaidOff: true,
  })

  it('is still listed, marked as paid off, so the player can see it is no longer charged', async () => {
    const { container, unmount } = await render('ready', [settled])

    expect(text(container)).toContain('Paid off')

    unmount()
  })

  it('shows dashes rather than figures for payments that are no longer owed', async () => {
    const { container, unmount } = await render('ready', [settled])

    const body = text(container)
    // A settled loan still showing "$96,000.00 monthly" would imply money is still going out.
    expect(body).not.toContain('$96,000.00')
    expect(body).not.toContain('0 of 60 mo')
    expect(body).toContain('—')

    unmount()
  })

  it('offers no repay button, because there is nothing left to repay', async () => {
    const { container, unmount } = await render('ready', [settled])

    expect(queryByRole(container, 'button', { name: 'Repay' })).toBeNull()

    unmount()
  })

  it('offers repay for the active loan only when both are listed together', async () => {
    const { container, unmount } = await render('ready', [loan(), settled])

    expect(queryAllByRole(container, 'button', { name: 'Repay' })).toHaveLength(1)

    unmount()
  })
})
