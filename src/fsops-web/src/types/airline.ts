export type StrategyProfile = 'International' | 'Domestic' | 'LowCost' | 'Premium' | 'Balanced'
export type AircraftFamily = 'A320' | 'B737'

/**
 * The figures behind one strategy profile - fetched from GET /airline/strategy-profiles, which
 * sources them from the same economy-config.json the pricing/demand engine reads and the same
 * advisory rules the route preview actually applies. Never hand-write these numbers in the
 * frontend: they exist so the profile picker can never describe a strategy differently to how it
 * behaves.
 */
export interface StrategyProfileInfo {
  profile: StrategyProfile
  /** Relative to 1.0 = the neutral baseline fare for a route's distance. */
  referenceFareMultiplier: number
  /** Demand's sensitivity to fare - higher means seats empty faster as fare rises above the reference. */
  elasticity: number
  /** Typical load factor at the suggested fare, as a fraction (0.73 = 73%). */
  baselineLoadFactor: number
  /** Relative to 1.0 = baseline operating cost. */
  costMultiplier: number
  /** Whether this profile raises an advisory when a route crosses an international border. */
  warnsOnInternationalSector: boolean
  /** Whether this profile raises an advisory when a route is a short domestic hop. */
  warnsOnShortDomesticHop: boolean
}

export interface StartingLoanInput {
  amount: number
  termMonths: number
  annualRatePct: number
}

export interface CreateAirlineInput {
  name: string
  icaoCode: string
  homeAirportIcao: string
  strategyProfile: StrategyProfile
  accentColour: string
  starterAircraftFamily: AircraftFamily
  currencyCode: string
  startingLoan?: StartingLoanInput
}

export interface Airline {
  id: string
  name: string
  icaoCode: string
  homeAirportIcao: string
  strategyProfile: StrategyProfile
  accentColour: string
  starterAircraftFamily: AircraftFamily
  currencyCode: string
}

export interface AirlineSummary {
  airline: Airline
  cashBalance: number
  fleetCount: number
  routeCount: number
  pilotCount: number
}
