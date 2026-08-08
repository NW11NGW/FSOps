export type FleetOwnership = 'Owned' | 'Leased'
export type FleetStatus = 'Active' | 'InMaintenance' | 'InFlight'
export type AircraftCondition = 'New' | 'Used'

/** One fleet aircraft, enriched for the Fleet page - see GET /fleet. */
export interface FleetAircraftSummary {
  id: string
  registration: string
  aircraftTypeId: string
  aircraftTypeName: string
  family: string
  paxCapacity: number
  ownership: FleetOwnership
  status: FleetStatus
  locationIcao: string
  airframeHours: number
  hoursSinceACheck: number
  hoursSinceCCheck: number
  hoursToNextACheck: number
  hoursToNextCCheck: number
  conditionPercent: number
  fuelOnBoardKg: number
  groundedUntilUtc: string | null
  groundedReason: string | null
  createdUtc: string
}

/**
 * One buyable/leasable aircraft type, with new/used pricing and exactly what a used example would
 * start at - see GET /fleet/aircraft-types. The used* fields let the buy dialog show the
 * condition/age trade-off before purchase, never as a surprise afterward.
 */
export interface AircraftTypeOption {
  id: string
  icaoType: string
  family: string
  manufacturer: string
  name: string
  paxCapacity: number
  rangeNm: number
  purchasePriceNew: number
  purchasePriceUsed: number
  monthlyLeaseRate: number
  /**
   * Months of monthlyLeaseRate charged as the up-front deposit for THIS airline's playstyle (1 in
   * Casual, 2 in True-life) - the same figure LeaseAsync actually charges. Never assume 1 month in
   * the UI; derive the deposit preview from this, not a hardcoded constant.
   */
  leaseDepositMonths: number
  usedAirframeHours: number
  usedHoursSinceACheck: number
  usedHoursSinceCCheck: number
  usedConditionPercent: number
  aCheckIntervalHours: number
  cCheckIntervalHours: number
}

export interface FleetLoan {
  id: string
  airlineId: string
  principal: number
  annualInterestRate: number
  termMonths: number
  monthlyPayment: number
  remainingBalance: number
  startUtc: string
  isPaidOff: boolean
  createdUtc: string
}

/** What the airline could currently borrow - see GET /fleet/loan-eligibility. */
export interface LoanEligibility {
  trailing30DayNetOperatingCashFlow: number
  maxMonthlyPayment: number
  maxDebtServiceFraction: number
}

/**
 * A live preview for a specific amount/term - see GET /fleet/loan-quote. annualRatePct is always
 * computed server-side (see docs/PLAN.md "Loan interest is set by the simulation, never by the
 * player"); the same figures POST /fleet/loans will actually charge if submitted unchanged.
 */
export interface LoanQuote {
  annualRatePct: number
  monthlyPayment: number
  totalInterest: number
  isEligible: boolean
  maxMonthlyPayment: number
  trailing30DayNetOperatingCashFlow: number
  maxDebtServiceFraction: number
}
