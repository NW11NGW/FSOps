import { beforeEach, describe, expect, it, vi } from 'vitest'

import { LoanRepaymentDialog } from './LoanRepaymentDialog'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'
import type { FinanceLoan, LoanSettlementQuote } from '@/types/finance'

// The dialog and the settings/quote hooks it depends on all import from '@/lib/api' - mocking the
// module once here covers every network call the tree makes, so the test controls exactly what
// each endpoint returns instead of hitting a real (nonexistent, in this test run) server.
vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post } from '@/lib/api'

const loan: FinanceLoan = {
  loanId: 'loan-1',
  principal: 500000,
  remainingBalance: 300000,
  annualInterestRate: 5.5,
  monthlyPayment: 4200,
  startUtc: '2026-01-01T00:00:00Z',
  termMonths: 60,
  remainingTermMonths: 40,
  totalInterestRemaining: 20000,
  isPaidOff: false,
}

const quote: LoanSettlementQuote = {
  loanId: 'loan-1',
  remainingBalance: 300000,
  earlySettlementFee: 1500,
  totalPayoff: 301500,
  interestSaved: 8000,
}

function mockGetDefault() {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return {
        currencyCode: 'USD',
        distanceUnit: 'Nm',
        altitudeUnit: 'Feet',
        weightUnit: 'Kg',
        timeDisplay: 'Utc',
        use24HourClock: true,
        theme: 'dark',
        communityFolderPath: null,
        simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path.includes('/settlement-quote')) return quote as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

beforeEach(() => {
  mockGetDefault()
  vi.mocked(post).mockReset()
})

describe('LoanRepaymentDialog - pay off in full', () => {
  it('sends the confirmed payoff figure the player was shown, not an empty body', async () => {
    // This is the exact shape of the shipped bug: handlePayFull posting no body at all to an
    // endpoint that requires `expectedTotalPayoff`. If that regresses, this assertion catches it.
    vi.mocked(post).mockResolvedValue({ loanId: 'loan-1', totalPayoff: 301500, cashBalance: 100000 })

    const onOpenChange = vi.fn()
    const onSuccess = vi.fn()
    const mounted = await mount(
      <SettingsProvider>
        <LoanRepaymentDialog loan={loan} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    // "Pay off -" (footer submit button) rather than "Pay off" alone, which also matches the
    // "Pay off in full" tab trigger.
    const payButton = findButton(document.body, 'Pay off -')
    click(payButton)
    await flush()

    expect(post).toHaveBeenCalledWith('/finance/loans/loan-1/settle', { expectedTotalPayoff: 301500 })
    expect(onSuccess).toHaveBeenCalled()
    expect(onOpenChange).toHaveBeenCalledWith(false)

    mounted.unmount()
  })
})

describe('LoanRepaymentDialog - reset behaviour', () => {
  it('does NOT wipe an in-progress amount when the parent merely re-renders with the same loan', async () => {
    // This is the wiped-input bug's exact shape: a page that re-renders on a live heartbeat
    // builds `loan` as a fresh object literal every time, so a reset effect keyed on object
    // identity would clear the field on every tick even though the player is mid-keystroke.
    const onOpenChange = vi.fn()
    const onSuccess = vi.fn()
    const mounted = await mount(
      <SettingsProvider>
        <LoanRepaymentDialog loan={loan} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    click(findButton(document.body, 'Partial overpayment'))
    await flush()
    const amountInput = document.body.querySelector<HTMLInputElement>('#overpay-amount')
    if (!amountInput) throw new Error('overpay-amount input not found')
    typeInto(amountInput, '1234')
    expect(amountInput.value).toBe('1234')

    // A fresh object literal, same loanId - simulates the parent page re-rendering on a heartbeat.
    const sameLoanNewReference: FinanceLoan = { ...loan }
    await mounted.rerender(
      <SettingsProvider>
        <LoanRepaymentDialog loan={sameLoanNewReference} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    const amountInputAfter = document.body.querySelector<HTMLInputElement>('#overpay-amount')
    expect(amountInputAfter?.value).toBe('1234')

    mounted.unmount()
  })

  it('DOES reset the mode/amount/error state when it opens for a genuinely different loan', async () => {
    const onOpenChange = vi.fn()
    const onSuccess = vi.fn()
    const mounted = await mount(
      <SettingsProvider>
        <LoanRepaymentDialog loan={loan} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    click(findButton(document.body, 'Partial overpayment'))
    await flush()
    const amountInput = document.body.querySelector<HTMLInputElement>('#overpay-amount')
    if (!amountInput) throw new Error('overpay-amount input not found')
    typeInto(amountInput, '1234')

    const otherLoan: FinanceLoan = { ...loan, loanId: 'loan-2', remainingBalance: 90000 }
    await mounted.rerender(
      <SettingsProvider>
        <LoanRepaymentDialog loan={otherLoan} onOpenChange={onOpenChange} onSuccess={onSuccess} />
      </SettingsProvider>,
    )
    await flush()

    // A genuinely different loan must land back on the "full" tab with a clean amount field.
    expect(document.body.querySelector<HTMLInputElement>('#overpay-amount')).toBeNull()

    mounted.unmount()
  })
})
