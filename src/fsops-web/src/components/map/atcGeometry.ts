import type { Feature, FeatureCollection, Polygon } from 'geojson'

import type { LonLat } from '@/types/route'
import type { VatsimAtcController } from '@/types/operations'

const EARTH_RADIUS_NM = 3440.065

/**
 * Approximate coverage circle (equirectangular, not geodesic) around a controller's airport,
 * radius from the feed's own `visual_range`. This is cosmetic sector shading, not navigation data
 * - FSOps holds no real FIR/TRACON boundary geometry, and at the airport scale this covers
 * (tens to low hundreds of nm) the distortion is not visually meaningful. Pulled out of
 * LiveOpsMap.tsx (which is mount-effect/MapLibre heavy and not unit-testable) so the geometry
 * itself can be tested in isolation.
 */
export function circlePolygon(lat: number, lon: number, radiusNm: number, steps = 48): LonLat[] {
  const angularRadius = radiusNm / EARTH_RADIUS_NM
  const latRad = (lat * Math.PI) / 180
  const cosLat = Math.cos(latRad) || 1e-6
  const points: LonLat[] = []
  for (let i = 0; i <= steps; i++) {
    const theta = (i / steps) * 2 * Math.PI
    const dLat = angularRadius * Math.cos(theta)
    const dLon = (angularRadius * Math.sin(theta)) / cosLat
    points.push([lon + (dLon * 180) / Math.PI, lat + (dLat * 180) / Math.PI])
  }
  return points
}

/**
 * One coverage-circle polygon per controller that has both a resolved position and a reported
 * visual range. Controllers without a resolved airport (should not happen post-filter server-
 * side, but the type is nullable) or without a visual range are skipped rather than drawing a
 * degenerate/zero-radius shape.
 */
export function buildAtcSectorFeatures(controllers: VatsimAtcController[]): FeatureCollection<Polygon> {
  const features: Feature<Polygon>[] = []
  for (const controller of controllers) {
    if (controller.latitudeDeg == null || controller.longitudeDeg == null || !controller.visualRangeNm) continue
    const ring = circlePolygon(controller.latitudeDeg, controller.longitudeDeg, controller.visualRangeNm)
    features.push({ type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [ring] } })
  }
  return { type: 'FeatureCollection', features }
}

/** Stable identity for a controller marker across polls. */
export function controllerKeyFor(controller: VatsimAtcController): string {
  return controller.callsign
}
