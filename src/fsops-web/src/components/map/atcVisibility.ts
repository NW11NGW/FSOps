import type { VatsimAtcController, VatsimAtcResponse } from '@/types/operations'

/**
 * A rectangle in degrees. `west`/`east` come straight from MapLibre's `getBounds()` and are
 * deliberately NOT normalised: MapLibre reports a viewport panned past the antimeridian as, say,
 * west 170 / east 190, and that unwrapped form is what {@link candidateRects} needs to line the
 * viewport up with polygon coordinates that live in [-180, 180].
 */
export interface MapBounds {
  west: number
  south: number
  east: number
  north: number
}

/** Nautical miles per degree of latitude - close enough for a bounding box around a range circle. */
const NM_PER_DEGREE = 60

/**
 * The viewport plus copies shifted a full turn either way.
 *
 * Testing all three is what makes the antimeridian a non-event without splitting rectangles or
 * normalising polygons: a viewport at west 170 / east 190 catches a polygon drawn at -175 through
 * its -360 copy, and a viewport at -190/-170 catches one at +175 through its +360 copy. Every
 * comparison below then stays plain arithmetic on raw coordinates.
 */
function candidateRects(bounds: MapBounds): MapBounds[] {
  // A viewport zoomed far enough out to see everything matches everything - skip the geometry.
  if (bounds.east - bounds.west >= 360) {
    return [{ west: -180, east: 180, south: bounds.south, north: bounds.north }]
  }
  return [
    bounds,
    { ...bounds, west: bounds.west + 360, east: bounds.east + 360 },
    { ...bounds, west: bounds.west - 360, east: bounds.east - 360 },
  ]
}

function pointInRect(lon: number, lat: number, rect: MapBounds): boolean {
  return lon >= rect.west && lon <= rect.east && lat >= rect.south && lat <= rect.north
}

/** Even-odd ray casting, same rule as the server's own point-in-boundary test. */
function pointInRing(lon: number, lat: number, ring: number[][]): boolean {
  let inside = false
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i]!
    const b = ring[j]!
    if ((a[1]! > lat) !== (b[1]! > lat)) {
      const crossingLon = ((b[0]! - a[0]!) * (lat - a[1]!)) / (b[1]! - a[1]!) + a[0]!
      if (lon < crossingLon) inside = !inside
    }
  }
  return inside
}

function orientation(ax: number, ay: number, bx: number, by: number, cx: number, cy: number): number {
  const value = (by - ay) * (cx - bx) - (bx - ax) * (cy - by)
  return value === 0 ? 0 : value > 0 ? 1 : 2
}

function segmentsIntersect(
  a: number[], b: number[], c: number[], d: number[],
): boolean {
  const o1 = orientation(a[0]!, a[1]!, b[0]!, b[1]!, c[0]!, c[1]!)
  const o2 = orientation(a[0]!, a[1]!, b[0]!, b[1]!, d[0]!, d[1]!)
  const o3 = orientation(c[0]!, c[1]!, d[0]!, d[1]!, a[0]!, a[1]!)
  const o4 = orientation(c[0]!, c[1]!, d[0]!, d[1]!, b[0]!, b[1]!)
  return o1 !== o2 && o3 !== o4
}

/**
 * True when a ring and a rectangle overlap at all.
 *
 * All three cases have to be tested, and the third is the one that gets forgotten. A sector is
 * routinely far larger than the screen, so zooming into the middle of one gives you no ring
 * vertices on screen at all - the rectangle is simply swallowed, and only the corner-inside test
 * finds it. Conversely a long boundary edge can slice straight across the viewport with neither a
 * vertex inside nor a corner enclosed, which only the edge-crossing test finds.
 *
 * This is exactly why a centroid or single-point test would be wrong: it would make the biggest
 * sectors disappear precisely when the user zooms in on them.
 */
export function ringIntersectsRect(ring: number[][], rect: MapBounds): boolean {
  if (ring.length === 0) return false

  for (const position of ring) {
    if (pointInRect(position[0]!, position[1]!, rect)) return true
  }

  const corners: number[][] = [
    [rect.west, rect.south],
    [rect.east, rect.south],
    [rect.east, rect.north],
    [rect.west, rect.north],
  ]

  if (pointInRing(corners[0]![0]!, corners[0]![1]!, ring)) return true

  for (let i = 0; i < ring.length - 1; i++) {
    for (let c = 0; c < 4; c++) {
      if (segmentsIntersect(ring[i]!, ring[i + 1]!, corners[c]!, corners[(c + 1) % 4]!)) return true
    }
  }

  return false
}

/**
 * Whether any part of a boundary's geometry falls inside the viewport. Outer rings only: a
 * viewport sitting entirely within a hole would be a false positive, which would list a controller
 * whose airspace is genuinely delegated away right where you are looking. That is a rare, small
 * over-claim, and it is the safer direction to err - the alternative failure, hiding a sector that
 * is visibly drawn on screen, is the one the user would actually notice.
 */
export function boundaryIntersectsViewport(coordinates: number[][][][], bounds: MapBounds): boolean {
  const rects = candidateRects(bounds)
  for (const polygon of coordinates) {
    const outerRing = polygon[0]
    if (!outerRing) continue
    for (const rect of rects) {
      if (ringIntersectsRect(outerRing, rect)) return true
    }
  }
  return false
}

/**
 * Whether a terminal controller's marker or its approximate range circle is on screen. The circle
 * is included, not just the airport, so the list never omits someone whose shading the user can
 * plainly see near the edge of the map.
 */
export function terminalIntersectsViewport(controller: VatsimAtcController, bounds: MapBounds): boolean {
  const { latitudeDeg: lat, longitudeDeg: lon } = controller
  if (lat == null || lon == null) return false

  const radiusNm = controller.visualRangeNm ?? 0
  const latPadding = radiusNm / NM_PER_DEGREE
  // Guard the cosine near the poles, where a fixed distance spans an unbounded number of degrees.
  const lonPadding = radiusNm / (NM_PER_DEGREE * Math.max(Math.cos((lat * Math.PI) / 180), 0.01))

  for (const rect of candidateRects(bounds)) {
    const overlapsLat = lat + latPadding >= rect.south && lat - latPadding <= rect.north
    const overlapsLon = lon + lonPadding >= rect.west && lon - lonPadding <= rect.east
    if (overlapsLat && overlapsLon) return true
  }
  return false
}

/**
 * The controllers a user can actually see right now.
 *
 * The map and the list have to agree at all times, because the user is looking at both at once: a
 * list that hides a sector drawn on screen, or names one nowhere near it, is incoherent. So this
 * runs over data already in hand and is re-run on pan and zoom - it never triggers a fetch, and
 * the feed keeps its own cadence regardless of how much the map is moved.
 *
 * With no viewport (`bounds` null) this returns the controllers unchanged. That is the in-game
 * panel's case: it has no map, so there is nothing to agree with, and the server has already
 * scoped its response to the airline's own network.
 */
export function filterControllersToViewport(
  controllers: VatsimAtcController[],
  boundaries: VatsimAtcResponse['boundaries'],
  bounds: MapBounds | null,
): VatsimAtcController[] {
  if (!bounds) return controllers

  return controllers.filter((controller) => {
    if (controller.coverageKind === 'sector') {
      // No geometry means nothing was drawn for this controller, so there is nothing on screen for
      // the list to agree with.
      const coordinates = controller.boundaryId ? boundaries?.[controller.boundaryId] : undefined
      return coordinates ? boundaryIntersectsViewport(coordinates, bounds) : false
    }
    return terminalIntersectsViewport(controller, bounds)
  })
}

/**
 * Display order: the airline's own airports first, then everything else, alphabetically within
 * each group. Network relevance stopped being a filter when the list started following the map,
 * but a controller at an airport you actually serve is still the more interesting one, so it keeps
 * its prominence rather than being lost among neighbours.
 */
export function sortControllersForDisplay(controllers: VatsimAtcController[]): VatsimAtcController[] {
  return [...controllers].sort((a, b) => {
    if (a.inNetwork !== b.inNetwork) return a.inNetwork ? -1 : 1
    return a.callsign.localeCompare(b.callsign)
  })
}
