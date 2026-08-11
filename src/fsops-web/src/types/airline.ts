export type StrategyProfile = 'International' | 'Domestic' | 'LowCost' | 'Premium' | 'Balanced'
export type AircraftFamily = 'A320' | 'B737'
export type Playstyle = 'Casual' | 'TrueLife'

/**
 * The figures and honest description behind one playstyle - fetched from GET /airline/playstyles,
 * which sources them from the same economy-config.json the founding lease/insurance/starting
 * capital actually come from. Chosen once at airline creation and permanent for that airline's
 * life - see docs on the onboarding Playstyle step and Settings -> Airline.
 */
export interface PlaystyleInfo {
  playstyle: Playstyle
  description: string
  immutable: boolean
  immutableReason: string
  startingCapital: number
  leaseDepositMonths: number
  starterLeaseRateA320: number
  starterLeaseRateB737: number
  monthlyInsurancePerAircraft: number
  /**
   * The rate a startup loan actually carries - never player-chosen (see docs/PLAN.md "Loan
   * interest is set by the simulation, never by the player"). Always equal to this playstyle's
   * loan rate ceiling: a brand-new airline has no trading history, so the backend always prices a
   * starting loan at the cap. Quoted here rather than re-derived so the onboarding review step can
   * never show a different figure than what the loan actually gets charged.
   */
  startingLoanAnnualRatePct: number
}

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

/**
 * Deliberately has NO rate field - see docs/PLAN.md "Loan interest is set by the simulation, never
 * by the player". The backend computes the rate; there is nothing here to send it.
 */
export interface StartingLoanInput {
  amount: number
  termMonths: number
}

export interface CreateAirlineInput {
  name: string
  icaoCode: string
  homeAirportIcao: string
  strategyProfile: StrategyProfile
  playstyle: Playstyle
  accentColour: string
  starterAircraftFamily: AircraftFamily
  currencyCode: string
  startingLoan?: StartingLoanInput
  /** The player's own name for their founding pilot - blank/omitted falls back to a sensible default server-side. */
  pilotName?: string
}

export interface Airline {
  id: string
  name: string
  icaoCode: string
  homeAirportIcao: string
  strategyProfile: StrategyProfile
  playstyle: Playstyle
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
  /** The player's own pilot name - lets Settings prefill "your name" without a separate /pilots fetch. */
  playerPilotName: string
}

export type ReputationDirection = 'improving' | 'declining' | 'steady' | 'new'
export type LandingQuality = 'smooth' | 'firm' | 'hard'

/**
 * GET /airline/reputation - the dashboard's reputation card. `direction` is 'new' only when there
 * are no recent Completed/Cancelled/Skipped sectors to judge a trend from (a brand-new airline).
 * `onTimePercent`/`landingQuality` are null when nothing in the recent window could measure that
 * signal honestly (e.g. only manually-completed flights with no telemetry) - never zero, since zero
 * would claim a bad outcome the data never actually showed.
 */
export interface ReputationSummary {
  score: number
  direction: ReputationDirection
  sectorsConsidered: number
  onTimePercent: number | null
  cancelledCount: number
  skippedCount: number
  landingQuality: LandingQuality | null
  /** Relative to 1.0 = baseline demand at reputation 50 - the same multiplier DemandCalculator
   *  actually applies to every route right now. */
  demandMultiplier: number
}
