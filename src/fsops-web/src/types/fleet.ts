export type FleetOwnership = 'Owned' | 'Leased'
export type FleetStatus = 'Active' | 'InMaintenance' | 'InFlight'
export type AircraftCondition = 'New' | 'Used'
/** Coarse size/role grouping for the buy/lease dialog's filter chips - see AircraftTypeOption.category. */
export type AircraftCategory = 'Narrowbody' | 'Widebody' | 'Regional'

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
  /**
   * True when this aircraft is kept free for the player to fly, and never offered to virtual
   * pilots - see PUT /fleet/{id}/reservation. Player-controlled from the Fleet page; nothing else
   * in the app re-forces this flag once set.
   */
  reservedForPlayer: boolean
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
  category: AircraftCategory
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
 * computed server-side, never client-side; the same figures POST /fleet/loans will actually charge
 * if submitted unchanged.
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

/**
 * Getting rid of an aircraft, by selling it or ending its lease.
 * Present when this aircraft is on a virtual pilot's standing weekly schedule; when
 * non-null the disposal is blocked (`canSell`/`canEndLease` is false) and `blockReason` says which
 * pilot and how many legs/week, rather than silently unassigning it.
 */
export interface StandingScheduleConflict {
  pilotId: string
  pilotName: string
  scheduleEntryCount: number
}

/** GET /fleet/{id}/sale-quote - preview of selling this OWNED aircraft right now. Read-only and
 *  side-effect-free, safe to call on every render of a confirmation dialog. */
export interface SaleQuote {
  fleetAircraftId: string
  registration: string
  aircraftTypeName: string
  conditionPercent: number
  airframeHours: number
  newPrice: number
  resaleFactorApplied: number
  saleValue: number
  isGroundedForMaintenance: boolean
  isLastAircraft: boolean
  standingScheduleConflict: StandingScheduleConflict | null
  canSell: boolean
  blockReason: string | null
  /** Selling never pays off an outstanding loan - repayments continue as scheduled. Always
   *  server-authored so the wording can't drift from what SellAsync actually does. */
  loanNote: string
}

/** POST /fleet/{id}/sell response. */
export interface SaleResult {
  fleetAircraftId: string
  saleValue: number
  cashBalance: number
}

/** GET /fleet/{id}/lease-termination-quote - preview of ending this LEASED aircraft's lease right
 *  now. `nextScheduledPaymentUtc`/`currentPeriodStartUtc` are null only when no active lease was
 *  found (see `canEndLease`/`blockReason` in that case). */
export interface LeaseTerminationQuote {
  fleetAircraftId: string
  leaseId: string | null
  registration: string
  aircraftTypeName: string
  monthlyRate: number
  currentPeriodStartUtc: string | null
  nextScheduledPaymentUtc: string | null
  daysIntoCurrentPeriod: number
  proRataAmount: number
  earlyTerminationFee: number
  totalCharge: number
  isLastAircraft: boolean
  standingScheduleConflict: StandingScheduleConflict | null
  canEndLease: boolean
  blockReason: string | null
}

/** POST /fleet/{id}/end-lease response. */
export interface LeaseTerminationResult {
  fleetAircraftId: string
  proRataAmount: number
  earlyTerminationFee: number
  totalCharge: number
  cashBalance: number
}
