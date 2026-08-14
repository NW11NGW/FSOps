import type { PricingVerdict } from '@/types/planning'

/**
 * The one sentence about a fare that the player can disagree with.
 *
 * Written on the CLIENT rather than on the server, unlike every other advisory sentence in this app
 * (range, runway, opportunity reasons), and for one specific reason: this one quotes money. Money is
 * stored in a single base unit and converted only at the point of display, so a sentence composed on
 * the server would have to print a bare number in a currency it cannot know the reader has chosen.
 * The server returns the facts (see `PricingVerdict`) and the money passes through the same
 * `fmt.money` every other figure on the screen does.
 */
export function fareVerdictSentence(verdict: PricingVerdict, money: (baseAmount: number) => string): string {
  if (verdict.kind === 'NobodyBooks') {
    return 'Nobody books at this fare — it is high enough that the market walks away entirely.'
  }

  if (verdict.kind === 'AlreadyBest') {
    return (
      `This fare is as good as any sampled: about ${verdict.paxBooked} passengers ` +
      `(${verdict.loadFactorPercent.toFixed(0)}% full) and ${money(verdict.profit)} profit a sector` +
      (verdict.aircraftDependent ? ', on the aircraft assumed below.' : '.')
    )
  }

  // 'CouldEarnMore' - the three fields below are only ever populated for this case, so a null here
  // would be a server contract break rather than an ordinary absence; fall back to the fare itself
  // rather than rendering "undefined" at the player.
  const betterFare = verdict.betterFare ?? 0
  const extraProfit = verdict.extraProfit ?? 0
  const betterPax = verdict.betterFarePaxBooked ?? verdict.paxBooked

  return (
    `You are pricing ${verdict.pricedRelativeToSuggestion} the suggested fare. Charging ` +
    `${money(betterFare)} instead looks worth about ${money(extraProfit)} more profit a sector ` +
    `(${betterPax} passengers rather than ${verdict.paxBooked}), of the fares sampled below.`
  )
}
