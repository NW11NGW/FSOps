import { describe, expect, it } from 'vitest'

import { fareVerdictSentence } from './fareVerdict'
import type { PricingVerdict } from '@/types/planning'

/** A stand-in for `fmt.money` - deliberately not the real formatter, so these tests assert that the
 *  sentence RUNS money through the formatter rather than asserting a particular currency. */
const money = (base: number) => `[${base.toFixed(2)}]`

function verdict(overrides: Partial<PricingVerdict> = {}): PricingVerdict {
  return {
    kind: 'AlreadyBest',
    paxBooked: 166,
    loadFactorPercent: 92.2,
    profit: 4460.27,
    betterFare: null,
    betterFarePaxBooked: null,
    extraProfit: null,
    pricedRelativeToSuggestion: 'exactly at',
    aircraftDependent: false,
    ...overrides,
  }
}

describe('fareVerdictSentence', () => {
  it('says plainly when a fare is already as good as any sampled', () => {
    const sentence = fareVerdictSentence(verdict(), money)

    expect(sentence).toContain('as good as any sampled')
    expect(sentence).toContain('166 passengers')
    expect(sentence).toContain('[4460.27]')
  })

  it('names the better fare and what it is worth', () => {
    const sentence = fareVerdictSentence(
      verdict({
        kind: 'CouldEarnMore',
        profit: 4086.27,
        paxBooked: 108,
        betterFare: 65,
        betterFarePaxBooked: 166,
        extraProfit: 374,
        pricedRelativeToSuggestion: 'above',
      }),
      money,
    )

    expect(sentence).toContain('above the suggested fare')
    expect(sentence).toContain('[65.00]')
    expect(sentence).toContain('[374.00]')
    expect(sentence).toContain('166 passengers rather than 108')
  })

  it('does not pretend anyone is flying when nobody books', () => {
    const sentence = fareVerdictSentence(verdict({ kind: 'NobodyBooks', paxBooked: 0, profit: -5000 }), money)

    expect(sentence).toContain('Nobody books at this fare')
    expect(sentence).not.toContain('passengers rather than')
  })

  /** Money must always pass through the formatter - a bare number would be printed in whatever
   *  currency the writer happened to assume, which is exactly what the base-unit rule forbids. */
  it('never prints an unformatted money figure', () => {
    const sentences = [
      fareVerdictSentence(verdict(), money),
      fareVerdictSentence(
        verdict({ kind: 'CouldEarnMore', betterFare: 65, betterFarePaxBooked: 166, extraProfit: 374 }),
        money,
      ),
    ]

    for (const sentence of sentences) {
      // Every two-decimal figure must be wrapped by the stub formatter's brackets. A bare one
      // means a money value reached the sentence without passing through fmt.money.
      expect(sentence).not.toMatch(/(?<!\[)\b\d+\.\d{2}\b(?!\])/)
    }
  })

  it('flags that the figures depend on the aircraft only when the airline owns more than one type', () => {
    expect(fareVerdictSentence(verdict({ aircraftDependent: true }), money)).toContain('on the aircraft assumed below')
    expect(fareVerdictSentence(verdict({ aircraftDependent: false }), money)).not.toContain('aircraft assumed')
  })
})
