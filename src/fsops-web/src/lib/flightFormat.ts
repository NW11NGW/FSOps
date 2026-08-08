import type { FlightPhase } from '@/types/flight'

export const PHASE_LABELS: Record<FlightPhase, string> = {
  Preflight: 'Preflight',
  TaxiOut: 'Taxi out',
  TakeoffRoll: 'Takeoff',
  Climb: 'Climb',
  Cruise: 'Cruise',
  Descent: 'Descent',
  Approach: 'Approach',
  Landed: 'Landed',
  TaxiIn: 'Taxi in',
  Shutdown: 'Shutdown',
}

export const PHASE_ORDER: FlightPhase[] = [
  'Preflight',
  'TaxiOut',
  'TakeoffRoll',
  'Climb',
  'Cruise',
  'Descent',
  'Approach',
  'Landed',
  'TaxiIn',
  'Shutdown',
]

/** Formats an ISO timestamp as a clock time, honouring the user's UTC/local + 12h/24h settings. */
export function formatClockTime(iso: string | null, useUtc: boolean, use24Hour: boolean): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '—'
  return new Intl.DateTimeFormat('en-US', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: !use24Hour,
    timeZone: useUtc ? 'UTC' : undefined,
  }).format(date)
}

/** Minutes between two ISO timestamps, or null if either is missing. */
export function minutesBetween(startIso: string | null, endIso: string | null): number | null {
  if (!startIso || !endIso) return null
  const start = Date.parse(startIso)
  const end = Date.parse(endIso)
  if (Number.isNaN(start) || Number.isNaN(end)) return null
  return Math.max(0, (end - start) / 60000)
}

export type LandingVerdict = 'smooth' | 'firm' | 'hard'

export interface LandingQuality {
  /** Always negative (a sink rate), regardless of the sign the raw sample arrived with. */
  displayFpm: number
  magnitude: number
  verdict: LandingVerdict
  label: string
}

/** Roughly -100 to -200 fpm reads as smooth, beyond -400 fpm as hard - see PLAN.md's report-card spec. */
export function assessLanding(fpm: number): LandingQuality {
  const magnitude = Math.abs(fpm)
  const verdict: LandingVerdict = magnitude <= 200 ? 'smooth' : magnitude <= 400 ? 'firm' : 'hard'
  const label = verdict === 'smooth' ? 'Smooth touchdown' : verdict === 'firm' ? 'Firm touchdown' : 'Hard touchdown'
  return { displayFpm: -magnitude, magnitude, verdict, label }
}
