import type { StrategyProfileInfo } from '@/types/airline'

/**
 * Plain-language descriptions of a strategy profile's effects, computed from the live figures
 * GET /airline/strategy-profiles returns (which come from economy-config.json and
 * RoutePreviewCalculator) rather than hand-written alongside them. When a balance constant is
 * retuned, this copy changes with it automatically instead of silently drifting.
 */

/** The single most important line in any strategy picker: "strategy" reads as a restriction, but
 * it never has been one. Show this everywhere a profile is chosen or changed. */
export const STRATEGY_NEVER_BLOCKS_NOTICE =
  'Your strategy never blocks a route. You can fly anywhere with any profile — it changes fares, demand and costs, and may show an advisory note when a route doesn’t suit your profile. Range and runway-length warnings are separate physical limits and apply to every profile, Balanced included.'

function relativeToBaseline(multiplier: number): { atBaseline: boolean; pct: number; higher: boolean } {
  const pct = Math.round(Math.abs(multiplier - 1) * 100)
  return { atBaseline: pct === 0, pct, higher: multiplier > 1 }
}

export function describeFares(info: StrategyProfileInfo): string {
  const rel = relativeToBaseline(info.referenceFareMultiplier)
  if (rel.atBaseline) return 'Suggests the baseline fare for a route’s distance — no premium, no discount.'
  return `Suggests fares about ${rel.pct}% ${rel.higher ? 'higher' : 'lower'} than baseline.`
}

export function describeCosts(info: StrategyProfileInfo): string {
  const rel = relativeToBaseline(info.costMultiplier)
  if (rel.atBaseline) return 'Operating costs sit at the baseline.'
  return `Operating costs run about ${rel.pct}% ${rel.higher ? 'higher' : 'lower'} than baseline.`
}

export function describeLoadFactor(info: StrategyProfileInfo): string {
  return `Typically fills to about ${Math.round(info.baselineLoadFactor * 100)}% of seats at the suggested fare.`
}

/** Elasticity is the number that decides whether a pricing mistake is survivable, so it gets the
 * most useful phrasing rather than a raw figure. Thresholds are buckets, not a hardcoded profile
 * list, so a retuned or new profile still lands in a sensible bucket automatically. */
export function describeSensitivity(info: StrategyProfileInfo): string {
  const e = info.elasticity
  if (e >= 1.5) return 'Passengers are very sensitive to fare — a small increase empties seats fast.'
  if (e >= 1.25) return 'Passengers are fairly sensitive to fare — pricing has a real effect on demand.'
  if (e >= 1.1) return 'Passengers are moderately tolerant of higher fares.'
  return 'Passengers tolerate higher fares — pricing mistakes here are more survivable.'
}

export function describeAdvisories(info: StrategyProfileInfo): string {
  if (info.warnsOnInternationalSector) {
    return 'Raises an advisory note if a route crosses an international border.'
  }
  if (info.warnsOnShortDomesticHop) {
    return 'Raises an advisory note if a route is a short domestic hop.'
  }
  return 'Never raises a route-suitability advisory — every route is treated as a fit.'
}
