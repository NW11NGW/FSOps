import type { ReputationSummary } from '@/types/airline'

/** Maps the backend's reputation direction onto StatTile's up/down/flat vocabulary and a short
 *  player-facing label - kept as one pure function so the dashboard's tile and its fuller card
 *  can never describe the same trend two different ways. */
export function reputationTrendLabel(direction: ReputationSummary['direction']): { direction: 'up' | 'down' | 'flat'; label: string } {
  switch (direction) {
    case 'improving':
      return { direction: 'up', label: 'Improving' }
    case 'declining':
      return { direction: 'down', label: 'Declining' }
    case 'new':
      return { direction: 'flat', label: 'No history yet' }
    case 'steady':
    default:
      return { direction: 'flat', label: 'Steady' }
  }
}

const LANDING_QUALITY_TEXT: Record<NonNullable<ReputationSummary['landingQuality']>, string> = {
  smooth: 'Landings have been smooth recently.',
  firm: 'Landings have been firm recently.',
  hard: 'Landings have been hard recently.',
}

/**
 * Plain-language sentences for "what is moving it" - docs/PLAN.md "Show what is driving it in the
 * player's own words". Never invents a driver the data can't support: on-time and landing are
 * omitted (not shown as 0%) when the backend couldn't measure them, and a brand-new airline gets an
 * honest "no sectors yet" line rather than a blank card.
 */
export function reputationDrivers(summary: ReputationSummary): string[] {
  if (summary.sectorsConsidered === 0) {
    return ['No completed sectors yet — your first flights start building a track record.']
  }

  const drivers: string[] = []

  if (summary.onTimePercent != null) {
    const sectorWord = summary.sectorsConsidered === 1 ? 'sector' : 'sectors'
    drivers.push(`${Math.round(summary.onTimePercent)}% on time over your last ${summary.sectorsConsidered} ${sectorWord}.`)
  }

  const disruptedParts: string[] = []
  if (summary.cancelledCount > 0) {
    disruptedParts.push(`${summary.cancelledCount} cancelled`)
  }
  if (summary.skippedCount > 0) {
    disruptedParts.push(`${summary.skippedCount} skipped`)
  }
  if (disruptedParts.length > 0) {
    drivers.push(`${disruptedParts.join(', ')} in that window.`)
  }

  if (summary.landingQuality) {
    drivers.push(LANDING_QUALITY_TEXT[summary.landingQuality])
  }

  if (drivers.length === 0) {
    drivers.push('Not enough measured sectors yet to say what is driving this.')
  }

  return drivers
}

/** e.g. 1.08 -> "+8% passenger demand from reputation.", 0.92 -> "-8% ...", 1.0 -> baseline line -
 *  ties the abstract score to its real consequence rather than leaving it as a bare number. */
export function reputationDemandLabel(demandMultiplier: number): string {
  const pct = Math.round((demandMultiplier - 1) * 100)
  if (pct === 0) {
    return 'Baseline passenger demand for your reputation.'
  }
  const sign = pct > 0 ? '+' : ''
  return `${sign}${pct}% passenger demand from reputation.`
}
