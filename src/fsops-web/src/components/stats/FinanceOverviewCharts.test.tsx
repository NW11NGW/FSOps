import { describe, expect, it } from 'vitest'

import { FinanceOverviewCharts } from './FinanceOverviewCharts'
import { mount, text } from '@/test/domHarness'
import type { FinanceCosts } from '@/types/finance'

function costs(overrides: Partial<{ revenue: number; fixed: number; variable: number }> = {}): FinanceCosts {
  const { revenue = 0, fixed = 0, variable = 0 } = overrides
  return {
    periodDays: 30,
    fixed: { leasePayments: fixed, salaries: 0, insurance: 0, loanPayments: 0, leaseEarlyTermination: 0, loanEarlySettlement: 0, total: fixed },
    variable: {
      fuel: variable,
      landingFees: 0,
      handling: 0,
      parkingFees: 0,
      passengerCharges: 0,
      turnaroundFees: 0,
      maintenance: 0,
      crew: 0,
      cancellationFees: 0,
      repositioning: 0,
      total: variable,
    },
    revenue: { ticketRevenue: revenue, onlineFlyingBonus: 0, aircraftSaleProceeds: 0, total: revenue },
    legacyDataNotice: null,
  }
}

const fmtMoney = (amount: number) => `£${amount.toFixed(2)}`

describe('FinanceOverviewCharts - revenue vs cost breakdown', () => {
  it('shows an empty state instead of a chart when costs is null', async () => {
    const { unmount } = await mount(<FinanceOverviewCharts costs={null} routes={[]} fmtMoney={fmtMoney} />)

    expect(text(document.body)).toContain('No revenue or costs posted yet')

    unmount()
  })

  it('shows an empty state instead of inventing an axis over three genuine zeros', async () => {
    // A brand-new airline before its first ledger post: costs has already loaded and is not null,
    // but revenue, fixed and variable are all exactly 0. A chart still has to draw SOME Y-axis for
    // three zero-height bars - there is no real data to anchor a scale on - and the result reads as
    // "we measured this and it came to nothing" rather than "nothing has been measured yet". This is
    // the regression: the empty state must win here exactly as it does for costs === null.
    const { unmount } = await mount(<FinanceOverviewCharts costs={costs()} routes={[]} fmtMoney={fmtMoney} />)

    expect(text(document.body)).toContain('No revenue or costs posted yet')

    unmount()
  })

  it('does not show the empty state once there is real data, even if only fixed costs have posted', async () => {
    // Realistic early state: a starter lease deposit has posted (fixed != 0) before any flight has
    // ever completed (revenue and variable still 0, routes still empty). This is genuine data, not
    // an all-zero chart, so it must render normally.
    const { unmount } = await mount(<FinanceOverviewCharts costs={costs({ fixed: -30000 })} routes={[]} fmtMoney={fmtMoney} />)

    expect(text(document.body)).not.toContain('No revenue or costs posted yet')

    unmount()
  })

  it('does not show the breakdown empty state once revenue has posted', async () => {
    const { unmount } = await mount(<FinanceOverviewCharts costs={costs({ revenue: 500, fixed: -200, variable: -50 })} routes={[]} fmtMoney={fmtMoney} />)

    expect(text(document.body)).not.toContain('No revenue or costs posted yet')

    unmount()
  })
})
