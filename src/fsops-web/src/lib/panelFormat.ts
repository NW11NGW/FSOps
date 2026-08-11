import type { FlightOption } from '@/types/flight'
import type { Flight } from '@/types/flight'
import type { FleetAircraftSummary } from '@/types/fleet'

/** Minutes of block time still to run, never negative - mirrors LiveFlightView's own elapsed/
 *  planned math so the panel's "time remaining" reads the same as the Fly screen's. */
export function remainingBlockMinutes(elapsedMinutes: number, plannedMinutes: number): number {
  return Math.max(0, plannedMinutes - elapsedMinutes)
}

/** Percent of planned block time flown so far, clamped to 100 - same formula LiveFlightView uses
 *  for its progress bar. */
export function progressPercent(elapsedMinutes: number, plannedMinutes: number): number {
  if (plannedMinutes <= 0) return 0
  return Math.min(100, (elapsedMinutes / plannedMinutes) * 100)
}

/** Projects an ETA by adding minutes of block time remaining onto "now" - the same elapsed/
 *  planned split the live hub already broadcasts, just read forward instead of backward. Not a
 *  second telemetry path: it consumes the identical LiveFlightSnapshot fields the Fly screen's
 *  block-time card already renders. */
export function projectEtaIso(nowIso: string, minutesRemaining: number): string {
  const now = Date.parse(nowIso)
  if (Number.isNaN(now)) return nowIso
  return new Date(now + minutesRemaining * 60000).toISOString()
}

/** actual - planned, in minutes. Positive means behind plan, negative means ahead - the same sign
 *  convention as ReportCard's DeltaLabel. */
export function blockVarianceMinutes(actualMinutes: number, plannedMinutes: number): number {
  return actualMinutes - plannedMinutes
}

/** A flight's net financial outcome. Flight.Revenue/TotalCost are posted by
 *  FlightEconomicsPoster from the exact same figures as the itemised ledger lines (ticket revenue
 *  minus landing/handling/parking/passenger/turnaround/maintenance/crew/fuel), so this always
 *  matches what the full report card's ledger sum would show without fetching the ledger itself. */
export function netEarned(flight: Pick<Flight, 'revenue' | 'totalCost'>): number {
  return flight.revenue - flight.totalCost
}

/** True once GET /flights (most-recent-first) has cooled down enough that surfacing it as "the
 *  debrief" - rather than the idle ready-to-fly state - is still an honest read of "just landed".
 *  Lets the panel recover a debrief on its own reload (the in-game toolbar panel can be closed and
 *  reopened independently of the server), not only while it was open to catch the flightCompleted
 *  hub event live. */
export function isRecentlyLanded(flight: Pick<Flight, 'status' | 'inUtc'>, nowIso: string, windowMinutes = 15): boolean {
  if (flight.status !== 'Completed' || !flight.inUtc) return false
  const inMs = Date.parse(flight.inUtc)
  const nowMs = Date.parse(nowIso)
  if (Number.isNaN(inMs) || Number.isNaN(nowMs)) return false
  const deltaMs = nowMs - inMs
  return deltaMs >= 0 && deltaMs <= windowMinutes * 60000
}

/** ~4% of each check's interval (economy-config.json: 500h A-check, 4000h C-check) - close enough
 *  to be one more sector or two away, not a routine "hundreds of hours to go" figure. */
const A_CHECK_WARNING_HOURS = 20
const C_CHECK_WARNING_HOURS = 150

export interface MaintenanceWarning {
  aircraft: FleetAircraftSummary
  checkType: 'A' | 'C'
  hoursRemaining: number
}

/**
 * The single most urgent "about to need a check" warning worth a glance on the panel, or null if
 * nothing in the fleet is close. Skips aircraft already InMaintenance - those are already being
 * dealt with, so flagging them again isn't a warning, it's old news.
 */
export function selectMaintenanceWarning(fleet: FleetAircraftSummary[]): MaintenanceWarning | null {
  let best: MaintenanceWarning | null = null
  for (const aircraft of fleet) {
    if (aircraft.status === 'InMaintenance') continue
    if (aircraft.hoursToNextACheck <= A_CHECK_WARNING_HOURS) {
      if (!best || aircraft.hoursToNextACheck < best.hoursRemaining) {
        best = { aircraft, checkType: 'A', hoursRemaining: aircraft.hoursToNextACheck }
      }
    }
    if (aircraft.hoursToNextCCheck <= C_CHECK_WARNING_HOURS) {
      if (!best || aircraft.hoursToNextCCheck < best.hoursRemaining) {
        best = { aircraft, checkType: 'C', hoursRemaining: aircraft.hoursToNextCCheck }
      }
    }
  }
  return best
}

/** Up to `limit` flyable routes for the panel's "next up" strip - the same flyable-routes list the
 *  Fly screen offers (GET /flights/options), just capped for a glanceable read. */
export function selectNextFlights(options: FlightOption[], limit = 3): FlightOption[] {
  return options.filter((option) => option.isFlyable).slice(0, limit)
}
