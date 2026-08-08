import type { ComponentType } from 'react'
import { AlertTriangle, CheckCircle2, HelpCircle, type LucideProps } from 'lucide-react'

import { distanceNm } from '@/lib/flightGeo'
import { cn } from '@/lib/utils'
import type { FlightOptionAircraft, SimStatus, TelemetryPayload } from '@/types/flight'

type CheckLevel = 'ok' | 'warn' | 'unknown'

interface ReadinessCheck {
  label: string
  detail: string
  level: CheckLevel
}

interface ReadinessChecksProps {
  simStatus: SimStatus | null
  simStatusLoaded: boolean
  telemetry: TelemetryPayload | null
  selectedAircraft: FlightOptionAircraft | null
  departure: { icao: string; latitude: number; longitude: number } | null
}

/** Within this many nm of the departure airport counts as "parked there" for the readiness check. */
const PARKED_THRESHOLD_NM = 3

const LEVEL_ICON: Record<CheckLevel, ComponentType<LucideProps>> = {
  ok: CheckCircle2,
  warn: AlertTriangle,
  unknown: HelpCircle,
}

const LEVEL_COLOR: Record<CheckLevel, string> = {
  ok: 'text-success',
  warn: 'text-warning',
  unknown: 'text-muted-foreground',
}

function buildChecks(props: ReadinessChecksProps): ReadinessCheck[] {
  const { simStatus, simStatusLoaded, telemetry, selectedAircraft, departure } = props
  const checks: ReadinessCheck[] = []

  if (!simStatusLoaded) {
    checks.push({ label: 'Simulator connection', detail: 'Checking…', level: 'unknown' })
  } else if (simStatus?.state === 'Connected') {
    checks.push({ label: 'Simulator connection', detail: `Connected (${simStatus.sourceKind})`, level: 'ok' })
  } else {
    checks.push({
      label: 'Simulator connection',
      detail: simStatus ? `${simStatus.state} — telemetry won't be tracked until MSFS connects` : 'Not connected',
      level: 'warn',
    })
  }

  const expectedTypeLabel = selectedAircraft?.icaoType || selectedAircraft?.family || null

  if (!selectedAircraft) {
    checks.push({ label: 'Aircraft loaded in sim', detail: 'Pick an aircraft to compare', level: 'unknown' })
  } else if (!simStatus?.aircraftTitle) {
    checks.push({ label: 'Aircraft loaded in sim', detail: "Sim isn't reporting a loaded aircraft yet", level: 'unknown' })
  } else if (!expectedTypeLabel) {
    checks.push({
      label: 'Aircraft loaded in sim',
      detail: `Sim has "${simStatus.aircraftTitle}" loaded — this route's expected type isn't available yet to compare`,
      level: 'unknown',
    })
  } else {
    const title = simStatus.aircraftTitle.toLowerCase()
    const looksLikeMatch = title.includes(expectedTypeLabel.toLowerCase())
    checks.push({
      label: 'Aircraft loaded in sim',
      detail: looksLikeMatch
        ? `"${simStatus.aircraftTitle}" looks right for ${expectedTypeLabel}`
        : `Sim has "${simStatus.aircraftTitle}" loaded — route expects ${expectedTypeLabel}`,
      level: looksLikeMatch ? 'ok' : 'warn',
    })
  }

  if (!departure) {
    checks.push({ label: 'Parked at departure', detail: 'Pick a route to check', level: 'unknown' })
  } else if (!telemetry) {
    checks.push({ label: 'Parked at departure', detail: 'No position data from the sim yet', level: 'unknown' })
  } else {
    const distance = distanceNm(telemetry.latitude, telemetry.longitude, departure.latitude, departure.longitude)
    const parked = telemetry.onGround && distance <= PARKED_THRESHOLD_NM
    checks.push({
      label: 'Parked at departure',
      detail: parked
        ? `On the ground at ${departure.icao}`
        : telemetry.onGround
          ? `On the ground, ${Math.round(distance)} nm from ${departure.icao}`
          : `Aircraft is airborne, ${Math.round(distance)} nm from ${departure.icao}`,
      level: parked ? 'ok' : 'warn',
    })
  }

  return checks
}

/**
 * Pre-flight readiness signals - sim connection, loaded-aircraft match, and departure-airport
 * proximity. Every check is informational: it warns clearly but never disables "Start flight",
 * because the tracker records what actually happens regardless.
 */
export function ReadinessChecks(props: ReadinessChecksProps) {
  const checks = buildChecks(props)

  return (
    <ul className="space-y-2">
      {checks.map((check) => {
        const Icon = LEVEL_ICON[check.level]
        return (
          <li key={check.label} className="flex items-start gap-2.5 text-sm">
            <Icon className={cn('mt-0.5 size-4 shrink-0', LEVEL_COLOR[check.level])} />
            <div className="min-w-0">
              <p className="font-medium">{check.label}</p>
              <p className="text-xs text-muted-foreground">{check.detail}</p>
            </div>
          </li>
        )
      })}
    </ul>
  )
}
