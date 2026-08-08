export interface SimBriefParams {
  originIcao: string
  destinationIcao: string
  aircraftIcaoType?: string | null
  airlineIcaoCode?: string | null
  flightNumber?: string | null
  registration?: string | null
  passengers?: number | null
}

/**
 * Builds the SimBrief "custom dispatch" URL. Minimum required by SimBrief is orig/dest/type;
 * anything else omitted falls back to the user's own SimBrief defaults. No API key involved -
 * the user just needs to be signed in to SimBrief in whatever tab this opens.
 */
export function buildSimBriefUrl(params: SimBriefParams): string {
  const url = new URL('https://dispatch.simbrief.com/options/custom')
  url.searchParams.set('orig', params.originIcao)
  url.searchParams.set('dest', params.destinationIcao)
  if (params.aircraftIcaoType) url.searchParams.set('type', params.aircraftIcaoType)
  if (params.airlineIcaoCode) url.searchParams.set('airline', params.airlineIcaoCode)
  if (params.flightNumber) url.searchParams.set('fltnum', params.flightNumber)
  if (params.registration) url.searchParams.set('reg', params.registration)
  if (params.passengers) url.searchParams.set('pax', String(params.passengers))
  return url.toString()
}

/**
 * Opens SimBrief dispatch in the user's default system browser, not inside the app - their
 * SimBrief session lives there. Returns the URL it opened, mainly so callers can log/report it.
 */
export function openSimBrief(params: SimBriefParams): string {
  const url = buildSimBriefUrl(params)
  window.open(url, '_blank', 'noopener')
  return url
}
