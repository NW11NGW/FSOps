export type StrategyProfile = 'International' | 'Domestic' | 'LowCost' | 'Premium'
export type AircraftFamily = 'A320' | 'B737'

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
