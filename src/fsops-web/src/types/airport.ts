export type AirportSizeCategory = 'Small' | 'Medium' | 'Large' | 'Heliport' | 'Seaplane' | 'Closed'

export interface AirportSummary {
  icao: string
  iata: string | null
  name: string
  municipality: string | null
  country: string
  latitude: number
  longitude: number
  elevationFt: number
  sizeCategory: AirportSizeCategory
  hasScheduledService: boolean
  longestRunwayFt: number | null
}

export interface Runway {
  designator: string
  lengthFt: number
  widthFt: number
  surface: string
  headingTrue: number
  isLighted: boolean
  isClosed: boolean
}

export interface AirportDetail extends AirportSummary {
  runways: Runway[]
  /** Current price per kg at this airport. Fuel prices vary by region, which is what makes
   *  tankering worthwhile, so the figure has to be visible before departure. */
  fuelPricePerKg: number
}

export interface WorldDataStatus {
  seeded: boolean
  airportCount: number
  runwayCount: number
  /** A first-time seed of an empty database is running — the app has no airports yet. */
  importInProgress: boolean
  progressPercent: number
  /**
   * A refresh over already-seeded data is running. Deliberately separate from
   * `importInProgress`: every screen keeps working throughout a refresh, so nothing should warn
   * about missing airports while one runs. Optional because an older server build won't send it.
   */
  refreshInProgress?: boolean
  /** Short identity of the bundled data set the current rows came from, e.g. "v1-3f9a2c40". */
  dataVersion?: string | null
  /** ISO timestamp of when the world data was last imported or refreshed. */
  lastAppliedUtc?: string | null
}
