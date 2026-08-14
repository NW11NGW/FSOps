import { describe, expect, it } from 'vitest'

import { TrendCharts } from './TrendCharts'
import { mount, text } from '@/test/domHarness'
import type { StatsTrendPoint } from '@/types/stats'

function point(overrides: Partial<StatsTrendPoint> = {}): StatsTrendPoint {
  return {
    dateUtc: '2026-08-10',
    cashBalance: 250000,
    sectorsFlown: 2,
    onTimePercent: 100,
    loadFactorPercent: 72,
    reputation: null,
    reputationPressure: 68,
    ...overrides,
  }
}

const fmtMoney = (amount: number) => `$${Math.round(amount).toLocaleString('en-US')}`

describe('TrendCharts', () => {
  it('says where the cash line comes from, so the number is never a black box', async () => {
    const { unmount } = await mount(
      <TrendCharts points={[point()]} currentReputation={62} reputationRecordedDays={0} fmtMoney={fmtMoney} />,
    )

    const body = text(document.body)
    expect(body).toContain('summed from the ledger itself')
    expect(body).toContain('the same money the Finances page shows')

    unmount()
  })

  it('distinguishes recorded reputation from pressure in words, not just by line style', async () => {
    // "Pressure" is not reputation, and a reader who never opens the code must be able to tell the
    // difference. Both the label and what each one actually means have to be on screen.
    const { unmount } = await mount(
      <TrendCharts points={[point({ reputation: 61 })]} currentReputation={62} reputationRecordedDays={1} fmtMoney={fmtMoney} />,
    )

    const body = text(document.body)
    expect(body).toContain('is your actual score, written down once a day')
    expect(body).toContain('is not your reputation')
    expect(body).toContain('pulling it')
    expect(body).toContain('the same figure the dashboard')

    unmount()
  })

  it('says days the app was not running are missing rather than filled in', async () => {
    const { unmount } = await mount(
      <TrendCharts points={[point({ reputation: 61 })]} currentReputation={62} reputationRecordedDays={1} fmtMoney={fmtMoney} />,
    )

    expect(text(document.body)).toContain('days FSOps was not running are simply missing rather than filled in')

    unmount()
  })

  it('explains the empty reputation chart rather than drawing an axis over nothing', async () => {
    const { unmount } = await mount(
      <TrendCharts
        points={[point({ reputation: null, reputationPressure: null })]}
        currentReputation={50}
        reputationRecordedDays={0}
        fmtMoney={fmtMoney}
      />,
    )

    expect(text(document.body)).toContain('Nothing to plot yet')

    unmount()
  })

  it('says so when only the retroactive pressure line has anything in it yet', async () => {
    // The realistic state on the day this ships: weeks of flying to derive pressure from, and no
    // recorded scores at all. The reader has to be told why one line stops and the other does not.
    const { unmount } = await mount(
      <TrendCharts
        points={[point({ reputation: null, reputationPressure: 70 })]}
        currentReputation={55}
        reputationRecordedDays={0}
        fmtMoney={fmtMoney}
      />,
    )

    expect(text(document.body)).toContain('reputation only started being written down recently')

    unmount()
  })
})
