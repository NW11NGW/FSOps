import type { FinanceRoute } from '@/types/finance'
import type { RouteSummary } from '@/types/route'

/**
 * One direction of a city pair, with whatever P&L the window recorded for it.
 * `sectorsFlown === 0` means the leg exists but was not flown in the window - not that it lost
 * money, and not that it is new.
 */
export interface NetworkLeg {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  sectorsFlown: number
  revenue: number
  cost: number
  profit: number
  loadFactorPercent: number | null
}

/**
 * A city pair, drawn as one arc on the map. Both directions are combined because that is how the
 * app creates routes (always as a there-and-back pair) and how a player thinks about them - and
 * because two arcs between the same two airports would sit exactly on top of each other, so the
 * second would simply hide the first. Each direction's own figures are kept in `legs` so the
 * detail panel can still show which way round the money is being made.
 */
export interface NetworkLink {
  /** Stable id for the pair: the two ICAOs, sorted, joined - so the outbound and return legs
   *  always resolve to the same link regardless of which one was seen first. */
  pairKey: string
  /** The two airports, sorted, purely for labelling - a combined arc has no direction. */
  fromIcao: string
  toIcao: string
  distanceNm: number
  legs: NetworkLeg[]
  sectorsFlown: number
  revenue: number
  cost: number
  profit: number
  /** Profit divided by sectors flown. Null when nothing flew - dividing by zero sectors would
   *  produce a figure about no flying at all. */
  profitPerSector: number | null
  /** Profit as a percentage of revenue. Null when no revenue was posted, for the same reason. */
  marginPercent: number | null
  /** Passengers carried over seats offered, across both directions. Null when no sector in the
   *  window could be matched to a known aircraft type. */
  loadFactorPercent: number | null
}

/**
 * How a link is performing, as one of four states.
 *
 * The banding is deliberately anchored on two numbers the player can see for themselves - zero,
 * and their own network's average profit per sector - rather than on a threshold invented here.
 * `describeBand` turns each into the single sentence that has to justify the colour.
 */
export type NetworkBand = 'not-flown' | 'losing' | 'below-average' | 'above-average'

/** Design token each band paints with. Semantic tokens only - never a hardcoded colour. */
export const BAND_TOKEN: Record<NetworkBand, string> = {
  'not-flown': '--muted-foreground',
  losing: '--danger',
  'below-average': '--warning',
  'above-average': '--success',
}

/** Tailwind text colour class for each band, for legends and inline labels. */
export const BAND_TEXT_CLASS: Record<NetworkBand, string> = {
  'not-flown': 'text-muted-foreground',
  losing: 'text-danger',
  'below-average': 'text-warning',
  'above-average': 'text-success',
}

export const BAND_LABEL: Record<NetworkBand, string> = {
  'not-flown': 'Not flown yet',
  losing: 'Losing money',
  'below-average': 'Below your average',
  'above-average': 'Above your average',
}

/**
 * Builds one link per city pair from the saved routes and the window's per-route P&L.
 *
 * Every active route is represented, including ones with no flying in the window - those are the
 * whole point of a network map: a route you built and never flew is exactly the thing that is
 * invisible in a table sorted by profit.
 */
export function buildNetworkLinks(routes: RouteSummary[], finance: FinanceRoute[]): NetworkLink[] {
  const financeByRouteId = new Map(finance.map((row) => [row.routeId, row]))
  const byPair = new Map<string, NetworkLink>()

  for (const route of routes) {
    if (!route.isActive) continue

    const [fromIcao, toIcao] = [route.departureIcao, route.arrivalIcao].sort()
    const pairKey = `${fromIcao}-${toIcao}`
    const row = financeByRouteId.get(route.id)

    const leg: NetworkLeg = {
      routeId: route.id,
      departureIcao: route.departureIcao,
      arrivalIcao: route.arrivalIcao,
      flightNumber: route.flightNumber,
      sectorsFlown: row?.sectorsFlown ?? 0,
      revenue: row?.revenue ?? 0,
      cost: row?.cost ?? 0,
      profit: row?.profit ?? 0,
      loadFactorPercent: row?.loadFactorPercent ?? null,
    }

    const existing = byPair.get(pairKey)
    if (existing) {
      existing.legs.push(leg)
      continue
    }

    byPair.set(pairKey, {
      pairKey,
      fromIcao: fromIcao ?? route.departureIcao,
      toIcao: toIcao ?? route.arrivalIcao,
      distanceNm: route.distanceNm,
      legs: [leg],
      sectorsFlown: 0,
      revenue: 0,
      cost: 0,
      profit: 0,
      profitPerSector: null,
      marginPercent: null,
      loadFactorPercent: null,
    })
  }

  const links = Array.from(byPair.values())
  for (const link of links) {
    link.sectorsFlown = link.legs.reduce((sum, leg) => sum + leg.sectorsFlown, 0)
    link.revenue = link.legs.reduce((sum, leg) => sum + leg.revenue, 0)
    link.cost = link.legs.reduce((sum, leg) => sum + leg.cost, 0)
    link.profit = link.revenue - link.cost
    link.profitPerSector = link.sectorsFlown > 0 ? link.profit / link.sectorsFlown : null
    link.marginPercent = link.revenue > 0 ? (100 * link.profit) / link.revenue : null

    // Weighted by sectors so a direction flown ten times counts ten times as much as one flown
    // once - averaging the two percentages directly would let a single sector on the return leg
    // swing the pair's load factor as hard as a whole month of outbound flying.
    const measurable = link.legs.filter((leg) => leg.loadFactorPercent !== null && leg.sectorsFlown > 0)
    const measurableSectors = measurable.reduce((sum, leg) => sum + leg.sectorsFlown, 0)
    link.loadFactorPercent =
      measurableSectors > 0
        ? measurable.reduce((sum, leg) => sum + leg.loadFactorPercent! * leg.sectorsFlown, 0) / measurableSectors
        : null
  }

  // Busiest first, then alphabetically, so the list beside the map has a stable, meaningful order.
  return links.sort((a, b) => b.sectorsFlown - a.sectorsFlown || a.pairKey.localeCompare(b.pairKey))
}

/**
 * The airline's own average profit per sector across everything it actually flew in the window -
 * total profit over total sectors, not the mean of each route's average, so a route flown once
 * cannot weigh as heavily as one flown fifty times.
 *
 * Null when nothing flew at all: with no flying there is no average, and a fabricated zero would
 * make every unflown route look "average".
 */
export function networkAverageProfitPerSector(links: NetworkLink[]): number | null {
  const sectors = links.reduce((sum, link) => sum + link.sectorsFlown, 0)
  if (sectors === 0) return null
  const profit = links.reduce((sum, link) => sum + link.profit, 0)
  return profit / sectors
}

/**
 * Which band a link falls into.
 *
 * The rules, in order, and each one is the whole reason for its colour:
 * 1. Nothing flew in the window - there is nothing to judge, so it is not judged.
 * 2. It loses money per sector. Always the worst band regardless of anything else: flying it again
 *    makes the airline poorer.
 * 3. It earns, but less per sector than the airline's own average. The slot could be earning more.
 * 4. It earns at or above the airline's own average.
 *
 * Note that when the whole network is losing money the average is itself negative, so a route that
 * merely breaks even lands in band 4. That is correct - it genuinely is one of the better ones -
 * and `describeBand` states the average alongside, so the green is never mistaken for "healthy in
 * absolute terms".
 */
export function bandFor(link: NetworkLink, networkAverage: number | null): NetworkBand {
  if (link.sectorsFlown === 0 || link.profitPerSector === null) return 'not-flown'
  if (link.profitPerSector < 0) return 'losing'
  if (networkAverage !== null && link.profitPerSector < networkAverage) return 'below-average'
  return 'above-average'
}

/**
 * The one sentence that has to justify the colour. Every band names the figure it was judged on
 * and, where relevant, the figure it was judged against - so a player can always see why a route
 * is the colour it is without being told to trust it.
 */
export function describeBand(
  link: NetworkLink,
  networkAverage: number | null,
  fmtMoney: (amount: number) => string,
): string {
  const band = bandFor(link, networkAverage)
  const sectorWord = link.sectorsFlown === 1 ? 'sector' : 'sectors'

  if (band === 'not-flown') {
    return 'No sectors flown on this route in this window, so there is nothing to judge it on yet.'
  }

  const perSector = fmtMoney(Math.abs(link.profitPerSector!))
  const average = networkAverage === null ? null : fmtMoney(Math.abs(networkAverage))
  const averagePhrase =
    average === null
      ? ''
      : ` Your network average is ${networkAverage! < 0 ? `a loss of ${average}` : average} per sector.`

  if (band === 'losing') {
    return `Loses ${perSector} every sector across ${link.sectorsFlown} ${sectorWord} flown — flying it again costs you money.${averagePhrase}`
  }

  if (band === 'below-average') {
    return `Earns ${perSector} a sector across ${link.sectorsFlown} ${sectorWord} flown, less than your network average.${averagePhrase}`
  }

  return `Earns ${perSector} a sector across ${link.sectorsFlown} ${sectorWord} flown, at or above your network average.${averagePhrase}`
}
