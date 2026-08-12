import { describe, expect, it } from 'vitest'

import {
  buildAtcSectorFeatures,
  buildAtcTerminalFeatures,
  circlePolygon,
  controllerKeyFor,
  hasSectorCoverage,
  hasTerminalCoverage,
  unwrapRingLongitudes,
} from './atcGeometry'
import type { VatsimAtcController } from '@/types/operations'

function controller(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return {
    callsign: 'EGLL_TWR',
    facilityLabel: 'Tower',
    frequency: '118.500',
    coverageKind: 'terminal',
    airportIcao: 'EGLL',
    airportName: 'London Heathrow Airport',
    latitudeDeg: 51.4706,
    longitudeDeg: -0.4619,
    visualRangeNm: 50,
    boundaryId: null,
    boundaryName: null,
    inNetwork: true,
    logonTimeUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

function sector(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return controller({
    callsign: 'LON_CTR',
    facilityLabel: 'Center',
    frequency: '127.100',
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

/** A 10x10 degree box as GeoJSON MultiPolygon coordinates. */
const BOX: number[][][][] = [[[[0, 50], [10, 50], [10, 60], [0, 60], [0, 50]]]]

describe('circlePolygon', () => {
  it('returns a closed ring (first and last point coincide)', () => {
    const ring = circlePolygon(51.47, -0.46, 50)
    expect(ring.length).toBeGreaterThan(3)
    const [firstLon, firstLat] = ring[0]!
    const [lastLon, lastLat] = ring[ring.length - 1]!
    expect(lastLon).toBeCloseTo(firstLon, 6)
    expect(lastLat).toBeCloseTo(firstLat, 6)
  })

  it('every point is roughly radiusNm from the centre (within equirectangular tolerance)', () => {
    const lat = 51.47
    const lon = -0.46
    const radiusNm = 50
    const ring = circlePolygon(lat, lon, radiusNm, 24)
    const earthRadiusNm = 3440.065

    for (const [pointLon, pointLat] of ring) {
      // Haversine distance from centre to this ring point - should track the requested radius
      // closely at this scale (tens of nm), which is exactly the scale this shading is used at.
      const dLat = ((pointLat - lat) * Math.PI) / 180
      const dLon = ((pointLon - lon) * Math.PI) / 180
      const a =
        Math.sin(dLat / 2) ** 2 +
        Math.cos((lat * Math.PI) / 180) * Math.cos((pointLat * Math.PI) / 180) * Math.sin(dLon / 2) ** 2
      const distanceNm = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a)) * earthRadiusNm
      expect(distanceNm).toBeCloseTo(radiusNm, 0)
    }
  })

  it('produces a larger ring for a larger radius', () => {
    const small = circlePolygon(40, 10, 10, 16)
    const large = circlePolygon(40, 10, 100, 16)
    const spread = (ring: typeof small) => Math.max(...ring.map(([lon]) => Math.abs(lon - 10)))
    expect(spread(large)).toBeGreaterThan(spread(small))
  })

  it('does not divide by zero at the poles', () => {
    expect(() => circlePolygon(90, 0, 50)).not.toThrow()
    const ring = circlePolygon(90, 0, 50)
    expect(ring.every(([lon, lat]) => Number.isFinite(lon) && Number.isFinite(lat))).toBe(true)
  })
})

describe('buildAtcTerminalFeatures', () => {
  it('builds one approximate range circle per terminal controller', () => {
    const collection = buildAtcTerminalFeatures([controller(), controller({ callsign: 'EGPH_TWR' })])
    expect(collection.type).toBe('FeatureCollection')
    expect(collection.features).toHaveLength(2)
    expect(collection.features[0]!.geometry.type).toBe('Polygon')
  })

  it('never draws a circle for a sector controller, even though the feed reports a visual range', () => {
    // A 300 nm circle around a centre controller is exactly the misleading picture this whole
    // feature exists to remove.
    const collection = buildAtcTerminalFeatures([sector()])
    expect(collection.features).toHaveLength(0)
  })

  it('skips a controller with no resolved position', () => {
    const collection = buildAtcTerminalFeatures([controller({ latitudeDeg: null, longitudeDeg: null })])
    expect(collection.features).toHaveLength(0)
  })

  it('skips a controller with no (or zero) visual range', () => {
    const collection = buildAtcTerminalFeatures([
      controller({ visualRangeNm: null }),
      controller({ visualRangeNm: 0 }),
    ])
    expect(collection.features).toHaveLength(0)
  })

  it('returns an empty collection for an empty input, not an error', () => {
    expect(buildAtcTerminalFeatures([])).toEqual({ type: 'FeatureCollection', features: [] })
  })
})

describe('buildAtcSectorFeatures', () => {
  it('builds one multi-polygon feature from the referenced boundary geometry', () => {
    const collection = buildAtcSectorFeatures([sector()], { EGTT: BOX })

    expect(collection.features).toHaveLength(1)
    const feature = collection.features[0]!
    expect(feature.geometry.type).toBe('MultiPolygon')
    expect(feature.geometry.coordinates).toEqual(BOX)
    expect(feature.properties).toMatchObject({
      boundaryId: 'EGTT',
      boundaryName: 'London',
      callsigns: 'LON_CTR',
    })
  })

  it('merges controllers sharing one boundary into a single polygon that lists both', () => {
    // LON_N_CTR and LON_S_CTR work one region whose internal division FSOps has no data for.
    // Two stacked identical shapes would double the fill and imply two separate sectors.
    const collection = buildAtcSectorFeatures(
      [sector({ callsign: 'LON_N_CTR' }), sector({ callsign: 'LON_S_CTR' })],
      { EGTT: BOX },
    )

    expect(collection.features).toHaveLength(1)
    expect(collection.features[0]!.properties!.callsigns).toBe('LON_N_CTR, LON_S_CTR')
  })

  it('keeps genuinely different boundaries apart', () => {
    const collection = buildAtcSectorFeatures(
      [sector(), sector({ callsign: 'SCO_CTR', boundaryId: 'EGPX', boundaryName: 'Scottish' })],
      { EGTT: BOX, EGPX: BOX },
    )
    expect(collection.features).toHaveLength(2)
  })

  it('ignores terminal controllers entirely', () => {
    expect(buildAtcSectorFeatures([controller()], { EGTT: BOX }).features).toHaveLength(0)
  })

  it('draws nothing when the response carried no geometry', () => {
    // The list-only request (?geometry omitted). Showing an empty shape would be worse than none.
    expect(buildAtcSectorFeatures([sector()], null).features).toHaveLength(0)
  })

  it('skips a boundary the response referenced but did not include', () => {
    const collection = buildAtcSectorFeatures([sector({ boundaryId: 'MISSING' })], { EGTT: BOX })
    expect(collection.features).toHaveLength(0)
  })

  it('skips a boundary whose geometry is present but empty', () => {
    expect(buildAtcSectorFeatures([sector()], { EGTT: [] as unknown as number[][][][] }).features).toHaveLength(0)
  })
})

describe('unwrapRingLongitudes', () => {
  it('leaves an ordinary ring untouched', () => {
    const ring = [[0, 50], [10, 50], [10, 60], [0, 60], [0, 50]]
    expect(unwrapRingLongitudes(ring)).toEqual(ring)
  })

  it('continues past the antimeridian instead of jumping back across the map', () => {
    // A Pacific region: 170E -> 175E -> 180 -> -175 (which is 185E). Left alone, MapLibre draws a
    // band smeared right across the world.
    const ring = [[170, 0], [175, 0], [-175, 0], [-170, 0]]
    expect(unwrapRingLongitudes(ring)).toEqual([[170, 0], [175, 0], [185, 0], [190, 0]])
  })

  it('handles the westward crossing too', () => {
    const ring = [[-175, 0], [175, 0]]
    expect(unwrapRingLongitudes(ring)).toEqual([[-175, 0], [-185, 0]])
  })

  it('preserves latitudes exactly', () => {
    const ring = [[170, 12.5], [-175, -33.25]]
    expect(unwrapRingLongitudes(ring).map(([, lat]) => lat)).toEqual([12.5, -33.25])
  })

  it('returns an empty ring unchanged', () => {
    expect(unwrapRingLongitudes([])).toEqual([])
  })
})

describe('coverage predicates', () => {
  it('reports which kinds are actually on screen, so the legend only claims what is drawn', () => {
    expect(hasSectorCoverage([controller()])).toBe(false)
    expect(hasSectorCoverage([controller(), sector()])).toBe(true)
    expect(hasTerminalCoverage([sector()])).toBe(false)
    expect(hasTerminalCoverage([controller(), sector()])).toBe(true)
    expect(hasSectorCoverage([])).toBe(false)
    expect(hasTerminalCoverage([])).toBe(false)
  })
})

describe('controllerKeyFor', () => {
  it('uses the callsign as the stable identity', () => {
    expect(controllerKeyFor(controller({ callsign: 'EGKK_APP' }))).toBe('EGKK_APP')
  })
})
