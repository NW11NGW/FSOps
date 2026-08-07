import type { LonLat } from '@/types/route'

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
