import type { LonLat } from '@/types/route'

const DEFAULT_SAMPLE_COUNT = 48

function toRadians(degrees: number): number {
  return (degrees * Math.PI) / 180
}

function toDegrees(radians: number): number {
  return (radians * 180) / Math.PI
}

function lerp(a: number, b: number, f: number): number {
  return a + (b - a) * f
}

/** Haversine central angle (radians) between two points - radius-independent, so it doubles as the angular distance the slerp below needs. */
function centralAngleRad(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const phi1 = toRadians(lat1)
  const phi2 = toRadians(lat2)
  const deltaPhi = toRadians(lat2 - lat1)
  const deltaLambda = toRadians(lon2 - lon1)
  const a =
    Math.sin(deltaPhi / 2) * Math.sin(deltaPhi / 2) +
    Math.cos(phi1) * Math.cos(phi2) * Math.sin(deltaLambda / 2) * Math.sin(deltaLambda / 2)
  return 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

/**
 * Client-side port of the backend's GreatCircle.SamplePath (see FSOps.Core/Planning/GreatCircle.cs):
 * a spherical-earth slerp between two points. Used to draw every saved route's arc on the map
 * without a per-route round trip to /routes/preview - the backend only needs to be asked for the
 * one route currently being planned. Returns raw (unnormalised) longitudes, same as the backend;
 * pass through splitAntimeridian before drawing.
 */
export function sampleGreatCirclePath(
  lat1: number,
  lon1: number,
  lat2: number,
  lon2: number,
  sampleCount: number = DEFAULT_SAMPLE_COUNT,
): LonLat[] {
  const count = Math.max(2, sampleCount)
  const phi1 = toRadians(lat1)
  const lambda1 = toRadians(lon1)
  const phi2 = toRadians(lat2)
  const lambda2 = toRadians(lon2)

  const angularDistance = centralAngleRad(lat1, lon1, lat2, lon2)
  const sinAngular = Math.sin(angularDistance)

  const points: LonLat[] = []
  for (let i = 0; i < count; i++) {
    const f = i / (count - 1)

    // Coincident (or near-antipodal) endpoints make the slerp divide by ~zero - fall back to
    // linear interpolation instead, matching the backend's guard.
    if (Math.abs(sinAngular) < 1e-9) {
      points.push([lerp(lon1, lon2, f), lerp(lat1, lat2, f)])
      continue
    }

    const a = Math.sin((1 - f) * angularDistance) / sinAngular
    const b = Math.sin(f * angularDistance) / sinAngular

    const x = a * Math.cos(phi1) * Math.cos(lambda1) + b * Math.cos(phi2) * Math.cos(lambda2)
    const y = a * Math.cos(phi1) * Math.sin(lambda1) + b * Math.cos(phi2) * Math.sin(lambda2)
    const z = a * Math.sin(phi1) + b * Math.sin(phi2)

    const lat = Math.atan2(z, Math.sqrt(x * x + y * y))
    const lon = Math.atan2(y, x)

    points.push([toDegrees(lon), toDegrees(lat)])
  }

  return points
}

/**
 * Splits a raw great-circle path into separate segments wherever consecutive points jump more
 * than 180 degrees in longitude. The backend deliberately returns unnormalised longitudes
 * (see GreatCircle.SamplePath) and documents this split as the caller's job - without it, a
 * Pacific-crossing route draws a straight (wrong) line all the way across the map instead of
 * two short arcs at each edge.
 */
export function splitAntimeridian(path: LonLat[]): LonLat[][] {
  const first = path[0]
  if (!first) return []

  const segments: LonLat[][] = []
  let current: LonLat[] = [first]

  for (let i = 1; i < path.length; i++) {
    const point = path[i]
    const previous = path[i - 1]
    if (!point || !previous) continue
    if (Math.abs(point[0] - previous[0]) > 180) {
      segments.push(current)
      current = []
    }
    current.push(point)
  }
  segments.push(current)

  return segments.filter((segment) => segment.length > 1)
}

/**
 * Bounding box for a path that may cross the antimeridian. Longitudes are "unwrapped" into a
 * continuous sequence (allowed to run outside -180..180) before taking the min/max, which keeps
 * a Pacific-crossing route's bounds narrow and correctly centred instead of naively spanning
 * nearly the whole globe the way a raw min/max over wrapped longitudes would.
 */
export function boundsForPath(points: LonLat[]): [LonLat, LonLat] | null {
  const first = points[0]
  if (!first) return null

  let unwrappedLon = first[0]
  let prevRawLon = first[0]
  let minLon = unwrappedLon
  let maxLon = unwrappedLon
  let minLat = first[1]
  let maxLat = first[1]

  for (let i = 1; i < points.length; i++) {
    const point = points[i]
    if (!point) continue
    const [rawLon, lat] = point
    let delta = rawLon - prevRawLon
    if (delta > 180) delta -= 360
    else if (delta < -180) delta += 360
    unwrappedLon += delta
    prevRawLon = rawLon

    if (unwrappedLon < minLon) minLon = unwrappedLon
    if (unwrappedLon > maxLon) maxLon = unwrappedLon
    if (lat < minLat) minLat = lat
    if (lat > maxLat) maxLat = lat
  }

  return [
    [minLon, minLat],
    [maxLon, maxLat],
  ]
}
