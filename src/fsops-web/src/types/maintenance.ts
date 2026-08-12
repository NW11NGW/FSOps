export type MaintenanceCheckType = 'ACheck' | 'CCheck'

/** One entry on a pilot's standing schedule that uses the aircraft in question - see GET
 *  /fleet/{id}/maintenance-quote. Shown so "perform maintenance now" states plainly whose
 *  schedule it will interrupt, before the player commits. */
export interface AffectedScheduleEntry {
  pilotId: string
  pilotName: string
  dayOfWeek: string
  departureTimeUtc: string
  routeLabel: string
}

/** A quote for bringing one specific check (A or C) forward - see MaintenanceQuoteResponse.quotes.
 *  Always the FULL cost/downtime for that check type; never scaled down for however many hours
 *  were left on the current cycle - forfeiting those hours IS the trade-off for choosing when the
 *  downtime lands. */
export interface MaintenanceCheckQuote {
  type: MaintenanceCheckType
  cost: number
  downtimeHours: number
  hoursSinceCheck: number
  hoursRemainingUntilNatural: number
}

/** GET /fleet/{id}/maintenance-quote - everything needed to decide whether, and which check, to
 *  bring forward. Read-only and side-effect-free, safe to call on every render of a confirmation
 *  dialog. */
export interface MaintenanceQuoteResponse {
  fleetAircraftId: string
  registration: string
  currentStatus: string
  canPerform: boolean
  blockReason: string | null
  affectedSchedules: AffectedScheduleEntry[]
  quotes: MaintenanceCheckQuote[]
}

/** POST /fleet/{id}/perform-maintenance response. */
export interface PerformMaintenanceResult {
  fleetAircraftId: string
  registration: string
  type: MaintenanceCheckType
  groundedUntilUtc: string
  cost: number
  cashBalance: number
}

export interface LedgerCategoryTotal {
  category: string
  amount: number
  count: number
}

export interface VirtualPilotActivitySummary {
  sectorsFlown: number
  revenue: number
  cost: number
  net: number
}

export interface MaintenanceEventSummary {
  fleetAircraftId: string
  registration: string
  type: MaintenanceCheckType | 'Unscheduled'
  startUtc: string
  endUtc: string | null
  cost: number
}

/** A flight that was Skipped, Cancelled, or Suspended during the catch-up window - see
 *  UnresolvedOccurrences below. `status` distinguishes a schedule that suspended itself for
 *  maintenance from one that genuinely couldn't be flown as built. */
export interface UnflyableOccurrenceSummary {
  flightId: string
  status: 'Skipped' | 'Cancelled' | 'Suspended'
  routeLabel: string
  plannedDepartureUtc: string
  reason: string | null
}

/** One aircraft the startup reservation reconciler released because it was marked reserved for the
 *  player but also had active scheduled legs - the schedule was kept, only the flag changed. */
export interface ReservationReleaseSummary {
  fleetAircraftId: string
  registration: string
  scheduledLegCount: number
}

/** One aircraft the reconciler auto-reserved to replace a release that would otherwise have left
 *  the airline with nothing reserved. */
export interface ReservationFallbackSummary {
  fleetAircraftId: string
  registration: string
}

/** What the startup reservation reconciler did to this player's fleet, if anything - present only
 *  when it found and fixed a contradiction. `leftWithNoReservedAircraft` means a release left the
 *  airline with nothing reserved and no safe replacement existed (every remaining aircraft had
 *  scheduled legs of its own) - the player needs to reserve one explicitly on the Fleet screen. */
export interface ReservationChangeSummary {
  released: ReservationReleaseSummary[]
  fallbackReserved: ReservationFallbackSummary[]
  leftWithNoReservedAircraft: boolean
}

/** One wall-clock catch-up pass (economy clock or virtual-flight resolver) that threw instead of
 *  completing. Not cosmetic: a failed pass can mean real money movements (lease/salary/insurance
 *  charges, virtual-pilot flight settlement) have not been posted. */
export interface StartupCatchUpFailureSummary {
  service: string
  message: string
  occurredUtc: string
}

/**
 * GET /away-summary - what happened while the app was closed, for the window since the player last
 * acknowledged a summary. Catch-up has to be explained or it reads as a bug.
 * Read-only - never advances the server's cursor on its own; see POST /away-summary/acknowledge
 * for the only thing that does.
 *
 * `reservationChanges` and `catchUpFailures` are startup-time findings folded into this same
 * dialog rather than surfaced separately - see StartupReconciliationState's own doc on the server
 * for why there is deliberately only one "something happened while you weren't looking" mechanism.
 * They can make `hasSummary` true even when nothing else here does (e.g. a reload seconds after a
 * catch-up pass failed, well before the normal away-gap threshold).
 */
export interface AwaySummaryResponse {
  hasSummary: boolean
  sinceUtc: string
  untilUtc: string
  awayHours: number
  cashBalance: number
  chargesByCategory: LedgerCategoryTotal[]
  netLedgerChange: number
  virtualPilots: VirtualPilotActivitySummary
  maintenanceEvents: MaintenanceEventSummary[]
  unresolvedOccurrences: UnflyableOccurrenceSummary[]
  reservationChanges: ReservationChangeSummary | null
  catchUpFailures: StartupCatchUpFailureSummary[]
}
