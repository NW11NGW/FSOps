/** GET /finance/summary - the Finances page's headline picture. Cash and its 30-day trend, plus
 *  the current rolling billing period's income/expenditure/net. `currentPeriod*Utc` come straight
 *  from EconomyState.LastProcessedUtc (+30 days) - the SAME rolling clock EconomyClockService
 *  bills against, not a calendar month. Never label this "this month". */
export interface FinanceSummary {
  cashBalance: number
  cashBalance30DaysAgo: number
  currentPeriodStartUtc: string | null
  currentPeriodEndUtc: string | null
  currentPeriodIncome: number
  currentPeriodExpenditure: number
  currentPeriodNet: number
}

/** One active lease - see GET /finance/leases. `nextPaymentUtc` is the SAME airline-wide rolling
 *  clock date for every active lease (leases don't each carry an independent cycle); it is
 *  deliberately the same figure the lease-termination quote's `nextScheduledPaymentUtc` shows. */
export interface FinanceLease {
  leaseId: string
  fleetAircraftId: string
  registration: string
  aircraftTypeName: string
  monthlyRate: number
  startUtc: string
  nextPaymentUtc: string
}

export interface FinanceLeasesResponse {
  leases: FinanceLease[]
  totalMonthlyCommitment: number
}

/** One loan - see GET /finance/loans. `isPaidOff: true` loans are included (so the page can show
 *  "no longer being charged") but should be visually de-emphasised rather than hidden. */
export interface FinanceLoan {
  loanId: string
  principal: number
  remainingBalance: number
  annualInterestRate: number
  monthlyPayment: number
  startUtc: string
  termMonths: number
  remainingTermMonths: number
  totalInterestRemaining: number
  isPaidOff: boolean
}

/** GET /finance/loans/{id}/settlement-quote - full early payoff preview, shown before confirming. */
export interface LoanSettlementQuote {
  loanId: string
  remainingBalance: number
  earlySettlementFee: number
  totalPayoff: number
  interestSaved: number
}

/** POST /finance/loans/{id}/settle response. */
export interface LoanSettleResult {
  loanId: string
  totalPayoff: number
  cashBalance: number
}

/** POST /finance/loans/{id}/overpay response. `note` is server-authored and always says the same
 *  thing the settle path implies: overpayment shortens the term, the monthly payment is unchanged. */
export interface LoanOverpayResult {
  loanId: string
  amountApplied: number
  newRemainingBalance: number
  cashBalance: number
  note: string
}

/**
 * One pilot's cost/revenue picture over the requested trailing REAL-day window (not the billing
 * cycle) - see GET /finance/pilots?days=. Independent of when the airline last billed; answers "is
 * this pilot worth it lately" on its own clock.
 *
 * `revenue`/`operatingCost` are real ledger sums (money that actually moved). `estimatedTotalCost`/
 * `estimatedNetContribution` are NOT - they add the pilot's monthly salary prorated to the window,
 * and a prorated salary is never itself a ledger posting (the real monthly-salary line posts in
 * full, on the billing cycle, wherever that falls relative to this window). They are a run-rate for
 * COMPARING pilots, not a claim about cash that moved - named `estimated*` deliberately (backend
 * decision, 2026-08-09) so the UI never lets a user mistake them for a ledger figure. Show them
 * labelled as estimates, and point to the ledger for the literal number.
 *
 * Field names verified against the running FinanceEndpoints.PilotsAsync rather than against any
 * written contract - same reasoning as FinanceRoute below.
 */
export interface FinancePilot {
  pilotId: string
  name: string
  isPlayer: boolean
  monthlySalary: number
  sectorsFlown: number
  revenue: number
  operatingCost: number
  estimatedTotalCost: number
  estimatedNetContribution: number
}

export interface FinancePilotsResponse {
  periodDays: number
  pilots: FinancePilot[]
}

export interface FinanceFixedCosts {
  leasePayments: number
  salaries: number
  insurance: number
  loanPayments: number
  leaseEarlyTermination: number
  loanEarlySettlement: number
  total: number
}

export interface FinanceVariableCosts {
  fuel: number
  landingFees: number
  handling: number
  parkingFees: number
  passengerCharges: number
  turnaroundFees: number
  maintenance: number
  crew: number
  cancellationFees: number
  other: number
  total: number
}

export interface FinanceRevenueBreakdown {
  ticketRevenue: number
  aircraftSaleProceeds: number
  total: number
}

/** GET /finance/costs?days= - fixed/variable/revenue split for the given trailing window (default
 *  30 days). `legacyDataNotice` is non-null only when the window includes ledger rows posted
 *  before the fixed/variable split existed; show it as a small, unalarming caveat, never hide it. */
export interface FinanceCosts {
  periodDays: number
  fixed: FinanceFixedCosts
  variable: FinanceVariableCosts
  revenue: FinanceRevenueBreakdown
  legacyDataNotice: string | null
}

/**
 * One route's P&L for the queried window - see GET /finance/routes?days=.
 *
 * Field names verified directly against the running FinanceEndpoints.RoutesAsync. The handler
 * accepts the same `?days=` override /finance/costs does, which is why these fields carry no
 * "30Days" suffix - a parameterised window cannot honestly claim to be 30 days once someone
 * passes a different value. Always show whatever period was actually requested next to these
 * figures, or the numbers mean nothing.
 */
export interface FinanceRoute {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  sectorsFlown: number
  revenue: number
  cost: number
  profit: number
}

export interface FinanceRoutesResponse {
  routes: FinanceRoute[]
}

/** One posted ledger line - see GET /finance/ledger. `flightId` is null for lines not tied to a
 *  specific flight (leases, salaries, insurance, loan payments, purchases/sales). */
export interface LedgerTransactionEntry {
  id: string
  utc: string
  category: string
  amount: number
  description: string
  flightId: string | null
}

export interface LedgerPage {
  total: number
  transactions: LedgerTransactionEntry[]
}
