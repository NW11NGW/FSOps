import { describe, expect, it } from 'vitest'

import { buildAtcSectorFeatures, circlePolygon, controllerKeyFor } from './atcGeometry'
import type { VatsimAtcController } from '@/types/operations'

function controller(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return {
    callsign: 'EGLL_TWR',
    facilityLabel: 'Tower',
    frequency: '118.500',
    airportIcao: 'EGLL',
    airportName: 'London Heathrow Airport',
    latitudeDeg: 51.4706,
    longitudeDeg: -0.4619,
    visualRangeNm: 50,
    logonTimeUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

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

describe('buildAtcSectorFeatures', () => {
  it('builds one polygon feature per controller with a position and a visual range', () => {
    const collection = buildAtcSectorFeatures([controller(), controller({ callsign: 'EGPH_TWR' })])
    expect(collection.type).toBe('FeatureCollection')
    expect(collection.features).toHaveLength(2)
    expect(collection.features[0]!.geometry.type).toBe('Polygon')
  })

  it('skips a controller with no resolved position', () => {
    const collection = buildAtcSectorFeatures([controller({ latitudeDeg: null, longitudeDeg: null })])
    expect(collection.features).toHaveLength(0)
  })

  it('skips a controller with no (or zero) visual range', () => {
    const collection = buildAtcSectorFeatures([controller({ visualRangeNm: null }), controller({ visualRangeNm: 0 })])
    expect(collection.features).toHaveLength(0)
  })

  it('returns an empty collection for an empty input, not an error', () => {
    const collection = buildAtcSectorFeatures([])
    expect(collection).toEqual({ type: 'FeatureCollection', features: [] })
  })
})

describe('controllerKeyFor', () => {
  it('uses the callsign as the stable identity', () => {
    expect(controllerKeyFor(controller({ callsign: 'EGKK_APP' }))).toBe('EGKK_APP')
  })
})
