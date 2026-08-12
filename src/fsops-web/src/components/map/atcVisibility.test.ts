import { describe, expect, it } from 'vitest'

import {
  boundaryIntersectsViewport,
  filterControllersToViewport,
  ringIntersectsRect,
  sortControllersForDisplay,
  terminalIntersectsViewport,
  type MapBounds,
} from './atcVisibility'
import type { VatsimAtcController } from '@/types/operations'

function terminal(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return {
    callsign: 'EGLL_TWR',
    facilityLabel: 'Tower',
    frequency: '118.500',
    coverageKind: 'terminal',
    airportIcao: 'EGLL',
    airportName: 'London Heathrow Airport',
    latitudeDeg: 51.4775,
    longitudeDeg: -0.4614,
    visualRangeNm: 30,
    boundaryId: null,
    boundaryName: null,
    inNetwork: true,
    logonTimeUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function sector(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return terminal({
    callsign: 'LON_CTR',
    facilityLabel: 'Center',
    coverageKind: 'sector',
    airportIcao: null,
    airportName: null,
    latitudeDeg: null,
    longitudeDeg: null,
    visualRangeNm: 300,
    boundaryId: 'EGTT',
    boundaryName: 'London',
    ...overrides,
  })
}

/** Roughly the UK on screen. */
const UK_VIEW: MapBounds = { west: -10, south: 49, east: 3, north: 60 }
/** Roughly the UAE on screen - the case the user actually complained about. */
const UAE_VIEW: MapBounds = { west: 51, south: 22, east: 57, north: 27 }

const box = (west: number, south: number, east: number, north: number): number[][][][] => [
  [[[west, south], [east, south], [east, north], [west, north], [west, south]]],
]

describe('ringIntersectsRect', () => {
  const rect: MapBounds = { west: 0, south: 0, east: 10, north: 10 }

  it('finds a ring with a vertex inside the rectangle', () => {
    expect(ringIntersectsRect([[5, 5], [50, 5], [50, 50], [5, 5]], rect)).toBe(true)
  })

  it('finds a ring that entirely swallows the rectangle', () => {
    // The case a centroid or single-point test gets catastrophically wrong: zoom into the middle
    // of a large sector and there are no ring vertices on screen at all, so only the
    // corner-inside-polygon test finds it. The biggest sectors would otherwise vanish exactly
    // when the user zooms in on them.
    expect(ringIntersectsRect([[-90, -90], [90, -90], [90, 90], [-90, 90], [-90, -90]], rect)).toBe(true)
  })

  it('finds a long edge slicing across the rectangle with no vertex or corner enclosed', () => {
    // Enters left, exits right, well above and below the rect's own corners.
    const ring = [[-50, 4], [50, 4], [50, 6], [-50, 6], [-50, 4]]
    expect(ringIntersectsRect(ring, { west: 0, south: 0, east: 10, north: 5 })).toBe(true)
  })

  it('rejects a ring that is nowhere near', () => {
    expect(ringIntersectsRect([[100, 100], [110, 100], [110, 110], [100, 100]], rect)).toBe(false)
  })

  it('rejects an empty ring rather than throwing', () => {
    expect(ringIntersectsRect([], rect)).toBe(false)
  })
})

describe('boundaryIntersectsViewport', () => {
  it('shows a sector overlapping the view', () => {
    expect(boundaryIntersectsViewport(box(-8, 50, 2, 56), UK_VIEW)).toBe(true)
  })

  it('hides a sector on the other side of the world', () => {
    // The literal complaint: looking at the UK must not list a UAE sector.
    expect(boundaryIntersectsViewport(box(51, 22, 57, 27), UK_VIEW)).toBe(false)
  })

  it('shows the same sector once the user pans there', () => {
    expect(boundaryIntersectsViewport(box(51, 22, 57, 27), UAE_VIEW)).toBe(true)
  })

  it('counts a sector only partly in view', () => {
    // Most of it is off the left edge; a corner is on screen. It is drawn, so it must be listed.
    expect(boundaryIntersectsViewport(box(-40, 50, -8, 56), UK_VIEW)).toBe(true)
  })

  it('counts any part of a multi-polygon, not just the first', () => {
    const split: number[][][][] = [
      ...box(100, 10, 110, 20),
      ...box(-8, 50, 2, 56),
    ]
    expect(boundaryIntersectsViewport(split, UK_VIEW)).toBe(true)
  })

  it('matches across the antimeridian in both directions', () => {
    const pacific = box(170, -10, 179, 10)
    // MapLibre reports a viewport panned past the seam unwrapped, e.g. 175E to 185E (= 175W).
    expect(boundaryIntersectsViewport(pacific, { west: 175, south: -20, east: 185, north: 20 })).toBe(true)
    expect(boundaryIntersectsViewport(pacific, { west: -185, south: -20, east: -175, north: 20 })).toBe(true)
    expect(boundaryIntersectsViewport(pacific, UK_VIEW)).toBe(false)
  })

  it('matches everything when the whole world is on screen', () => {
    const whole: MapBounds = { west: -200, south: -85, east: 200, north: 85 }
    expect(boundaryIntersectsViewport(box(51, 22, 57, 27), whole)).toBe(true)
    expect(boundaryIntersectsViewport(box(-8, 50, 2, 56), whole)).toBe(true)
  })

  it('ignores geometry with no rings', () => {
    expect(boundaryIntersectsViewport([[]], UK_VIEW)).toBe(false)
    expect(boundaryIntersectsViewport([], UK_VIEW)).toBe(false)
  })
})

describe('terminalIntersectsViewport', () => {
  it('shows an airport inside the view', () => {
    expect(terminalIntersectsViewport(terminal(), UK_VIEW)).toBe(true)
  })

  it('hides an airport outside the view', () => {
    expect(terminalIntersectsViewport(terminal({ latitudeDeg: 25.25, longitudeDeg: 55.36 }), UK_VIEW)).toBe(false)
  })

  it('counts a range circle that reaches into the view even when the airport does not', () => {
    // The user can see the shading; the list must not pretend it is not there.
    const justOutside = terminal({ latitudeDeg: 51, longitudeDeg: 4.5, visualRangeNm: 120 })
    expect(terminalIntersectsViewport(justOutside, UK_VIEW)).toBe(true)
    const farOutside = terminal({ latitudeDeg: 51, longitudeDeg: 4.5, visualRangeNm: 5 })
    expect(terminalIntersectsViewport(farOutside, UK_VIEW)).toBe(false)
  })

  it('handles a missing position and a missing range without throwing', () => {
    expect(terminalIntersectsViewport(terminal({ latitudeDeg: null, longitudeDeg: null }), UK_VIEW)).toBe(false)
    expect(terminalIntersectsViewport(terminal({ visualRangeNm: null }), UK_VIEW)).toBe(true)
  })

  it('does not blow up near the poles', () => {
    const polar = terminal({ latitudeDeg: 89.9, longitudeDeg: 0, visualRangeNm: 200 })
    expect(() => terminalIntersectsViewport(polar, UK_VIEW)).not.toThrow()
  })
})

describe('filterControllersToViewport', () => {
  const boundaries = { EGTT: box(-8, 50, 2, 56), OMAE: box(51, 22, 57, 27) }

  it('keeps only what is on screen, mixing both coverage kinds', () => {
    const controllers = [
      terminal(),
      terminal({ callsign: 'OMDB_TWR', latitudeDeg: 25.25, longitudeDeg: 55.36 }),
      sector(),
      sector({ callsign: 'EMIRATES_CTR', boundaryId: 'OMAE', boundaryName: 'Emirates' }),
    ]

    const visible = filterControllersToViewport(controllers, boundaries, UK_VIEW)

    expect(visible.map((c) => c.callsign)).toEqual(['EGLL_TWR', 'LON_CTR'])
  })

  it('returns the other side of the world once the map is pointed there', () => {
    const controllers = [terminal(), sector({ callsign: 'EMIRATES_CTR', boundaryId: 'OMAE' })]
    const visible = filterControllersToViewport(controllers, boundaries, UAE_VIEW)
    expect(visible.map((c) => c.callsign)).toEqual(['EMIRATES_CTR'])
  })

  it('drops a sector whose geometry was never sent, because nothing was drawn for it', () => {
    expect(filterControllersToViewport([sector({ boundaryId: 'MISSING' })], boundaries, UK_VIEW)).toEqual([])
    expect(filterControllersToViewport([sector()], null, UK_VIEW)).toEqual([])
  })

  it('passes everything through when there is no viewport at all', () => {
    // The in-game panel: no map, so nothing to agree with, and the server has already scoped the
    // response to the airline's own network.
    const controllers = [terminal(), sector({ callsign: 'EMIRATES_CTR', boundaryId: 'OMAE' })]
    expect(filterControllersToViewport(controllers, boundaries, null)).toEqual(controllers)
  })
})

describe('sortControllersForDisplay', () => {
  it('puts the airline’s own airports first, alphabetically within each group', () => {
    const controllers = [
      terminal({ callsign: 'ZZZZ_TWR', inNetwork: false }),
      terminal({ callsign: 'BBBB_TWR', inNetwork: true }),
      terminal({ callsign: 'AAAA_TWR', inNetwork: false }),
      terminal({ callsign: 'CCCC_TWR', inNetwork: true }),
    ]

    expect(sortControllersForDisplay(controllers).map((c) => c.callsign)).toEqual([
      'BBBB_TWR', 'CCCC_TWR', 'AAAA_TWR', 'ZZZZ_TWR',
    ])
  })

  it('does not mutate its input', () => {
    const controllers = [terminal({ callsign: 'B', inNetwork: false }), terminal({ callsign: 'A', inNetwork: true })]
    sortControllersForDisplay(controllers)
    expect(controllers.map((c) => c.callsign)).toEqual(['B', 'A'])
  })
})
