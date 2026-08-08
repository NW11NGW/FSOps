const EARTH_RADIUS_NM = 3440.065

function toRadians(degrees: number): number {
  return (degrees * Math.PI) / 180
}

/** Haversine great-circle distance in nautical miles - used for the pre-flight "parked at departure" readiness check. */
export function distanceNm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const dLat = toRadians(lat2 - lat1)
  const dLon = toRadians(lon2 - lon1)
  const a =
    Math.sin(dLat / 2) ** 2 + Math.cos(toRadians(lat1)) * Math.cos(toRadians(lat2)) * Math.sin(dLon / 2) ** 2
  return 2 * EARTH_RADIUS_NM * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}
