import { describe, expect, it } from 'vitest'

import { bandFor, buildNetworkLinks, describeBand, networkAverageProfitPerSector } from './networkHealth'
import type { FinanceRoute } from '@/types/finance'
import type { RouteSummary } from '@/types/route'

function route(overrides: Partial<RouteSummary> = {}): RouteSummary {
  return {
    id: 'route-out',
    departureIcao: 'EGGD',
    departureName: 'Bristol',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh',
    flightNumber: '101',
    returnRouteId: 'route-back',
    distanceNm: 280,
    baseFare: 90,
    isActive: true,
    createdUtc: '2026-07-01T00:00:00Z',
    ...overrides,
  }
}

function financeRoute(overrides: Partial<FinanceRoute> = {}): FinanceRoute {
  return {
    routeId: 'route-out',
    departureIcao: 'EGGD',
    arrivalIcao: 'EGPH',
    flightNumber: '101',
    sectorsFlown: 10,
    revenue: 100000,
    cost: 60000,
    profit: 40000,
    paxFlown: 1200,
    seatsFlown: 1800,
    loadFactorPercent: 66.7,
    ...overrides,
  }
}

const fmtMoney = (amount: number) => `£${Math.round(amount).toLocaleString('en-US')}`

describe('buildNetworkLinks', () => {
  it('combines the two directions of a city pair into one link', () => {
    // Routes are always created as a there-and-back pair, and two arcs between the same two
    // airports would sit exactly on top of each other - the second would simply hide the first.
    const links = buildNetworkLinks(
      [
        route({ id: 'route-out', departureIcao: 'EGGD', arrivalIcao: 'EGPH' }),
        route({ id: 'route-back', departureIcao: 'EGPH', arrivalIcao: 'EGGD', returnRouteId: 'route-out' }),
      ],
      [
        financeRoute({ routeId: 'route-out', sectorsFlown: 6, revenue: 60000, cost: 30000, profit: 30000 }),
        financeRoute({ routeId: 'route-back', departureIcao: 'EGPH', arrivalIcao: 'EGGD', sectorsFlown: 4, revenue: 40000, cost: 38000, profit: 2000 }),
      ],
    )

    const link = links[0]!
    expect(links).toHaveLength(1)
    expect(link.pairKey).toBe('EGGD-EGPH')
    expect(link.sectorsFlown).toBe(10)
    expect(link.revenue).toBe(100000)
    expect(link.cost).toBe(68000)
    expect(link.profit).toBe(32000)
    expect(link.profitPerSector).toBe(3200)
    // Both directions are still individually available - a route that earns outbound and loses on
    // the way back is exactly the thing the map exists to reveal.
    expect(link.legs.map((leg) => leg.profit)).toEqual([30000, 2000])
  })

  it('includes routes with no flying in the window, as not-flown rather than as a loss', () => {
    // A route you built and never flew is invisible in a table sorted by profit. It is the single
    // most valuable thing a network map can surface, so it must appear.
    const links = buildNetworkLinks([route({ id: 'route-unflown', departureIcao: 'EGSS', arrivalIcao: 'EGPF', returnRouteId: null })], [])

    const link = links[0]!
    expect(links).toHaveLength(1)
    expect(link.sectorsFlown).toBe(0)
    expect(link.profitPerSector).toBeNull()
    expect(bandFor(link, 1000)).toBe('not-flown')
  })

  it('leaves out inactive routes', () => {
    expect(buildNetworkLinks([route({ isActive: false })], [])).toHaveLength(0)
  })

  it('weights a pair load factor by sectors, not by averaging the two percentages', () => {
    // Averaging the percentages directly would let a single return sector swing the pair's load
    // factor as hard as a whole month of outbound flying.
    const links = buildNetworkLinks(
      [
        route({ id: 'route-out' }),
        route({ id: 'route-back', departureIcao: 'EGPH', arrivalIcao: 'EGGD', returnRouteId: 'route-out' }),
      ],
      [
        financeRoute({ routeId: 'route-out', sectorsFlown: 9, loadFactorPercent: 90 }),
        financeRoute({ routeId: 'route-back', sectorsFlown: 1, loadFactorPercent: 10 }),
      ],
    )

    // (90*9 + 10*1) / 10 = 82, not the naive (90 + 10) / 2 = 50.
    expect(links[0]!.loadFactorPercent).toBeCloseTo(82, 5)
  })

  it('reports a null load factor when no sector could be matched to a known aircraft type', () => {
    const links = buildNetworkLinks([route({ returnRouteId: null })], [financeRoute({ loadFactorPercent: null })])
    expect(links[0]!.loadFactorPercent).toBeNull()
  })
})

describe('networkAverageProfitPerSector', () => {
  it('is total profit over total sectors, not the mean of each route average', () => {
    // A route flown once must not weigh as heavily as one flown fifty times.
    const links = buildNetworkLinks(
      [
        route({ id: 'a', departureIcao: 'EGGD', arrivalIcao: 'EGPH', returnRouteId: null }),
        route({ id: 'b', departureIcao: 'EGGD', arrivalIcao: 'EGSS', returnRouteId: null }),
      ],
      [
        financeRoute({ routeId: 'a', sectorsFlown: 1, revenue: 10000, cost: 0, profit: 10000 }),
        financeRoute({ routeId: 'b', departureIcao: 'EGGD', arrivalIcao: 'EGSS', sectorsFlown: 9, revenue: 9000, cost: 0, profit: 9000 }),
      ],
    )

    // (10000 + 9000) / 10 = 1900, not the mean of 10000/sector and 1000/sector (= 5500).
    expect(networkAverageProfitPerSector(links)).toBe(1900)
  })

  it('is null when nothing flew, rather than a zero that would make every route look average', () => {
    const links = buildNetworkLinks([route({ returnRouteId: null })], [])
    expect(networkAverageProfitPerSector(links)).toBeNull()
  })
})

describe('bandFor', () => {
  function linkWith(profit: number, sectors: number) {
    return buildNetworkLinks(
      [route({ returnRouteId: null })],
      [financeRoute({ sectorsFlown: sectors, revenue: 100000, cost: 100000 - profit, profit })],
    )[0]!
  }

  it('calls a loss-making route losing, whatever the network average is', () => {
    expect(bandFor(linkWith(-5000, 5), -10000)).toBe('losing')
    expect(bandFor(linkWith(-5000, 5), 10000)).toBe('losing')
  })

  it('separates below-average from at-or-above-average earners', () => {
    // 5,000 profit over 10 sectors is 500 a sector.
    expect(bandFor(linkWith(5000, 10), 400)).toBe('above-average')
    expect(bandFor(linkWith(5000, 10), 600)).toBe('below-average')
  })

  it('treats a route exactly on the average as above it, not below', () => {
    expect(bandFor(linkWith(10000, 10), 1000)).toBe('above-average')
  })

  it('calls an earning route above-average when nothing else flew to compare it with', () => {
    expect(bandFor(linkWith(5000, 10), null)).toBe('above-average')
  })

  it('bands against a negative network average without calling a break-even route a loss', () => {
    // With the whole network losing money, a route that merely breaks even genuinely IS one of the
    // better ones. describeBand states the negative average alongside so the colour is never read
    // as "healthy in absolute terms".
    const breakEven = linkWith(0, 4)
    expect(bandFor(breakEven, -2000)).toBe('above-average')
    expect(describeBand(breakEven, -2000, fmtMoney)).toContain('a loss of £2,000 per sector')
  })
})

describe('describeBand', () => {
  function linkWith(profit: number, sectors: number) {
    return buildNetworkLinks(
      [route({ returnRouteId: null })],
      [financeRoute({ sectorsFlown: sectors, revenue: 100000, cost: 100000 - profit, profit })],
    )[0]!
  }

  it('names the per-sector figure and the average it was judged against', () => {
    // The colour has to be justifiable in one sentence, and that sentence has to quote numbers the
    // player can see for themselves - not ask them to trust a threshold.
    const sentence = describeBand(linkWith(-8000, 4), 1500, fmtMoney)
    expect(sentence).toContain('Loses £2,000 every sector')
    expect(sentence).toContain('4 sectors flown')
    expect(sentence).toContain('costs you money')
    expect(sentence).toContain('Your network average is £1,500 per sector')
  })

  it('says there is nothing to judge for a route that did not fly', () => {
    const links = buildNetworkLinks([route({ returnRouteId: null })], [])
    expect(describeBand(links[0]!, 1000, fmtMoney)).toContain('nothing to judge it on yet')
  })

  it('uses the singular for a single sector', () => {
    expect(describeBand(linkWith(1000, 1), 500, fmtMoney)).toContain('1 sector flown')
  })
})
