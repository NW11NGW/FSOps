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
  /** Current price per kg at this airport - see docs/PLAN.md "Persistent fuel state and tankering". */
  fuelPricePerKg: number
}

export interface WorldDataStatus {
  seeded: boolean
  airportCount: number
  runwayCount: number
  importInProgress: boolean
  progressPercent: number
}
