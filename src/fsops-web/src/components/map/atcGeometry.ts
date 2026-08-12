import type { Feature, FeatureCollection, MultiPolygon, Polygon } from 'geojson'

import type { LonLat } from '@/types/route'
import type { VatsimAtcController, VatsimAtcResponse } from '@/types/operations'

const EARTH_RADIUS_NM = 3440.065

/**
 * Approximate coverage circle (equirectangular, not geodesic) around a terminal controller's
 * airport, radius from the feed's own `visual_range`.
 *
 * This is an approximation and the map says so: `visual_range` is how far the controller's client
 * is configured to see, not the shape of anything they control. It is kept for tower, ground,
 * delivery and airport-named approach because those positions genuinely are airport-local, so a
 * circle is a defensible sketch rather than an invention. En-route positions get real geometry
 * instead - see {@link buildAtcSectorFeatures}. At the scale a terminal circle covers (tens to low
 * hundreds of nm) the equirectangular distortion is not visually meaningful.
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
 * One approximate range circle per *terminal* controller. Sector controllers are skipped here even
 * though the feed reports a `visual_range` for them too - a 600 nm circle drawn around a centre
 * controller would be exactly the misleading picture this feature exists to remove. They have no
 * position on the wire either, so the guard is belt and braces.
 */
export function buildAtcTerminalFeatures(controllers: VatsimAtcController[]): FeatureCollection<Polygon> {
  const features: Feature<Polygon>[] = []
  for (const controller of controllers) {
    if (controller.coverageKind !== 'terminal') continue
    if (controller.latitudeDeg == null || controller.longitudeDeg == null || !controller.visualRangeNm) continue
    const ring = circlePolygon(controller.latitudeDeg, controller.longitudeDeg, controller.visualRangeNm)
    features.push({
      type: 'Feature',
      properties: { callsign: controller.callsign },
      geometry: { type: 'Polygon', coordinates: [ring] },
    })
  }
  return { type: 'FeatureCollection', features }
}

/**
 * Rewrites a ring so consecutive longitudes never jump more than 180 degrees, letting coordinates
 * run past +/-180 instead. Without this a region straddling the antimeridian renders as a band
 * smeared right across the map - visually obvious, but it is the kind of thing nobody notices
 * until a Pacific FIR is staffed.
 *
 * RFC7946 requires such geometry to be split into separate polygons on each side, and the bundled
 * data is meant to comply, so in practice this is a no-op: a ring that never jumps is returned
 * unchanged. It costs one pass and removes a whole class of failure that would otherwise depend on
 * upstream always being right.
 */
export function unwrapRingLongitudes(ring: number[][]): number[][] {
  const unwrapped: number[][] = []
  let offset = 0
  for (let i = 0; i < ring.length; i++) {
    const position = ring[i]!
    const lon = position[0]!
    if (i > 0) {
      const delta = lon - ring[i - 1]![0]!
      if (delta > 180) offset -= 360
      else if (delta < -180) offset += 360
    }
    unwrapped.push([lon + offset, position[1]!])
  }
  return unwrapped
}

/** A drawn sector: one published boundary plus every controller currently working it. */
export interface AtcSectorProperties {
  boundaryId: string
  boundaryName: string
  /** Comma-separated so it survives MapLibre's feature-property serialisation, which flattens
   *  arrays to strings on the way through `queryRenderedFeatures`. */
  callsigns: string
}

/**
 * One polygon per *published boundary*, not per controller.
 *
 * Two controllers splitting one region (LON_N_CTR and LON_S_CTR) are working one piece of airspace
 * whose internal division FSOps has no data for. Drawing two identical stacked shapes would double
 * the fill opacity and imply two separately-bounded sectors, so they are merged into one polygon
 * that lists both. Boundaries referenced by a controller but absent from `boundaries` (a
 * geometry-free response, or a mid-cycle inconsistency) are skipped - never drawn as an empty or
 * fabricated shape.
 */
export function buildAtcSectorFeatures(
  controllers: VatsimAtcController[],
  boundaries: VatsimAtcResponse['boundaries'],
): FeatureCollection<MultiPolygon> {
  const features: Feature<MultiPolygon>[] = []
  if (!boundaries) return { type: 'FeatureCollection', features }

  const callsignsByBoundary = new Map<string, { name: string; callsigns: string[] }>()
  for (const controller of controllers) {
    if (controller.coverageKind !== 'sector' || !controller.boundaryId) continue
    const existing = callsignsByBoundary.get(controller.boundaryId)
    if (existing) {
      existing.callsigns.push(controller.callsign)
    } else {
      callsignsByBoundary.set(controller.boundaryId, {
        name: controller.boundaryName ?? controller.boundaryId,
        callsigns: [controller.callsign],
      })
    }
  }

  for (const [boundaryId, { name, callsigns }] of callsignsByBoundary) {
    const coordinates = boundaries[boundaryId]
    if (!coordinates || coordinates.length === 0) continue
    features.push({
      type: 'Feature',
      properties: {
        boundaryId,
        boundaryName: name,
        callsigns: callsigns.join(', '),
      } satisfies AtcSectorProperties,
      geometry: {
        type: 'MultiPolygon',
        coordinates: coordinates.map((polygon) => polygon.map(unwrapRingLongitudes)),
      },
    })
  }

  return { type: 'FeatureCollection', features }
}

/** Stable identity for a controller marker across polls. */
export function controllerKeyFor(controller: VatsimAtcController): string {
  return controller.callsign
}

/** Whether anything in this response is drawn as a real published boundary - drives the map
 *  legend, which must only claim a sector row when a sector is actually on screen. */
export function hasSectorCoverage(controllers: VatsimAtcController[]): boolean {
  return controllers.some((c) => c.coverageKind === 'sector')
}

/** Whether anything is drawn as an approximate range circle. */
export function hasTerminalCoverage(controllers: VatsimAtcController[]): boolean {
  return controllers.some((c) => c.coverageKind === 'terminal')
}
