import type { LogbookSector } from '@/types/flight'

export type LogbookSortKey = 'date' | 'route' | 'aircraft' | 'blockDelta' | 'landing' | 'net'
export type SortDirection = 'asc' | 'desc'

export interface LogbookFilters {
  /** Free text matched against route, flight number, registration, aircraft type and pilot. */
  query: string
  /** 'all', or a specific FlightStatus. */
  status: string
  /** 'all' | 'mine' | 'crew' - who was flying. */
  flownBy: 'all' | 'mine' | 'crew'
  /** When true, only sectors that have a recorded flown track to look at. */
  withTrackOnly: boolean
}

export const DEFAULT_LOGBOOK_FILTERS: LogbookFilters = {
  query: '',
  status: 'all',
  flownBy: 'all',
  withTrackOnly: false,
}

/**
 * Minutes the sector ran over (positive) or under (negative) its planned block time. Null when
 * block time was not measurable - a missing Out or In stamp, or a sim that ran faster than real
 * time, in which case elapsed wall time means nothing and no delta can honestly be quoted.
 */
export function blockDeltaMinutes(sector: LogbookSector): number | null {
  return sector.actualBlockMinutes === null ? null : sector.actualBlockMinutes - sector.plannedBlockMinutes
}

function haystack(sector: LogbookSector): string {
  return [
    sector.departureIcao,
    sector.arrivalIcao,
    `${sector.departureIcao}${sector.arrivalIcao}`,
    `${sector.departureIcao}-${sector.arrivalIcao}`,
    sector.flightNumber,
    sector.registration,
    sector.aircraftTypeName,
    sector.aircraftIcaoType,
    sector.pilotName,
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
}

export function filterSectors(sectors: LogbookSector[], filters: LogbookFilters): LogbookSector[] {
  const query = filters.query.trim().toLowerCase()

  return sectors.filter((sector) => {
    if (filters.status !== 'all' && sector.status !== filters.status) return false
    if (filters.flownBy === 'mine' && !sector.isPlayerFlight) return false
    if (filters.flownBy === 'crew' && sector.isPlayerFlight) return false
    if (filters.withTrackOnly && !sector.hasTrack) return false
    if (query.length > 0 && !haystack(sector).includes(query)) return false
    return true
  })
}

/**
 * Sorts a copy of `sectors`.
 *
 * <b>A null always sorts last, in both directions.</b> Nulls here mean "not measured" - a landing
 * whose rate the sim never reported, a block time made meaningless by time acceleration - and
 * letting them float to the top of an ascending sort would present "we could not measure this" as
 * "this was the smallest", which is the specific misreading this whole feature exists to prevent.
 */
export function sortSectors(sectors: LogbookSector[], key: LogbookSortKey, direction: SortDirection): LogbookSector[] {
  const sign = direction === 'asc' ? 1 : -1

  const compareNumbers = (a: number | null, b: number | null): number => {
    if (a === null && b === null) return 0
    if (a === null) return 1
    if (b === null) return -1
    return sign * (a - b)
  }

  const compareText = (a: string, b: string): number => sign * a.localeCompare(b)

  return [...sectors].sort((a, b) => {
    switch (key) {
      case 'date':
        return compareNumbers(Date.parse(a.dateUtc) || null, Date.parse(b.dateUtc) || null)
      case 'route':
        return compareText(`${a.departureIcao}${a.arrivalIcao}`, `${b.departureIcao}${b.arrivalIcao}`)
      case 'aircraft':
        return compareText(a.registration ?? '', b.registration ?? '')
      case 'blockDelta':
        return compareNumbers(blockDeltaMinutes(a), blockDeltaMinutes(b))
      case 'landing':
        // Compared by sink-rate magnitude so "ascending" means smoothest first, which is what a
        // pilot means by sorting landings. The stored value's sign varies by source.
        return compareNumbers(
          a.landingFpmFirst === null ? null : Math.abs(a.landingFpmFirst),
          b.landingFpmFirst === null ? null : Math.abs(b.landingFpmFirst),
        )
      case 'net':
        return compareNumbers(a.net, b.net)
      default:
        return 0
    }
  })
}

/** Totals for whatever is currently in view, so the footer describes the filtered set rather than
 *  the whole logbook - a filtered table whose total ignores the filter is worse than no total. */
export interface LogbookTotals {
  sectors: number
  net: number
  blockMinutes: number
  /** Sectors whose block time could not be measured, so the hours figure can say it is a floor
   *  rather than silently under-reporting. */
  unmeasuredBlockSectors: number
}

export function totalsFor(sectors: LogbookSector[]): LogbookTotals {
  return {
    sectors: sectors.length,
    net: sectors.reduce((sum, s) => sum + s.net, 0),
    blockMinutes: sectors.reduce((sum, s) => sum + (s.actualBlockMinutes ?? 0), 0),
    unmeasuredBlockSectors: sectors.filter((s) => s.actualBlockMinutes === null).length,
  }
}
