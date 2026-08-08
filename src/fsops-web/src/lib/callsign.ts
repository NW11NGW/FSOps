/**
 * Formats a flight's operator callsign - the airline's ICAO code plus the route's numeric flight
 * number, e.g. "OLA" + "502" -> "OLA502" - matching how operators actually write flight numbers.
 * Route.FlightNumber itself stays purely numeric in storage; this composes the display form only,
 * so the airline's identity is never duplicated into route rows.
 *
 * Falls back to the bare number when the airline hasn't loaded yet or its ICAO code is missing,
 * rather than rendering "undefined502". Returns null only when there's no flight number at all.
 */
export function formatCallsign(icaoCode: string | null | undefined, flightNumber: string | null | undefined): string | null {
  if (!flightNumber) return null
  return icaoCode ? `${icaoCode}${flightNumber}` : flightNumber
}
