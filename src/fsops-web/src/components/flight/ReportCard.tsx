import { AlertTriangle, ArrowRight, Clock3, Fuel, Gauge, Info, RotateCw, Target } from 'lucide-react'

import { LandingGauge } from '@/components/flight/LandingGauge'
import { PhaseTimeline } from '@/components/flight/PhaseTimeline'
import { StatTile } from '@/components/shared/StatTile'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { useSettings } from '@/hooks/useSettings'
import { minutesBetween } from '@/lib/flightFormat'
import { cn } from '@/lib/utils'
import type { FlightDetail, FlightPhase, MismatchPayload } from '@/types/flight'

interface ReportRouteInfo {
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
}

interface ReportCardProps {
  detail: FlightDetail
  route: ReportRouteInfo | null
  className?: string
}

interface DeltaLabelProps {
  actual: number
  planned: number
  unitLabel: string
  /** True when a smaller actual value is the good outcome (block time, fuel burn). */
  lowerIsBetter: boolean
  betterLabel: string
  worseLabel: string
}

function DeltaLabel({ actual, planned, unitLabel, lowerIsBetter, betterLabel, worseLabel }: DeltaLabelProps) {
  const delta = actual - planned
  if (Math.abs(delta) < 0.5) {
    return <span className="text-muted-foreground">On plan</span>
  }
  const isLower = delta < 0
  const better = lowerIsBetter ? isLower : !isLower
  return (
    <span className={better ? 'text-success' : 'text-warning'}>
      {delta > 0 ? '+' : ''}
      {Math.round(delta)} {unitLabel} ({better ? betterLabel : worseLabel})
    </span>
  )
}

/** Honest "how far did this flight get" for report-card phase display - the OOOI fields it
 *  actually captured, not an assumption that every flight reached Shutdown (abandoned and
 *  complete-with-estimates flights may not have). */
function inferFinalPhase(flight: FlightDetail['flight']): FlightPhase {
  if (flight.inUtc) return 'Shutdown'
  if (flight.onUtc) return 'Landed'
  if (flight.offUtc) return 'Climb'
  if (flight.outUtc) return 'TaxiOut'
  return 'Preflight'
}

/**
 * The post-flight hero moment: landing quality first, then the phase timeline with OOOI, then
 * actual-vs-planned, then an informational (never punitive) aircraft-type badge. No money is
 * shown - the economy engine that would make Revenue/TotalCost real numbers isn't built yet, and
 * this app never displays a fabricated figure.
 */
export function ReportCard({ detail, route, className }: ReportCardProps) {
  const { flight, events } = detail
  const { fmt } = useSettings()

  const bounceCount = Math.max(0, events.filter((e) => e.type === 'Touchdown').length - 1)
  const mismatchEvent = events.find((e) => e.type === 'Mismatch')
  let mismatchDetail: MismatchPayload | null = null
  if (mismatchEvent) {
    try {
      mismatchDetail = JSON.parse(mismatchEvent.payloadJson) as MismatchPayload
    } catch {
      mismatchDetail = null
    }
  }

  const actualBlockMinutes = minutesBetween(flight.outUtc, flight.inUtc)
  const hasLanding = flight.landingFpmFirst !== null

  const title = route ? `${route.departureIcao} → ${route.arrivalIcao}` : 'Flight report'
  const subtitle = route ? [route.departureName, route.arrivalName].filter(Boolean).join(' → ') : null

  return (
    <div className={cn('space-y-4', className)}>
      <Card>
        <CardHeader className="flex-row items-start justify-between gap-4 space-y-0">
          <div>
            <CardTitle className="font-mono text-xl">{title}</CardTitle>
            {subtitle && <p className="mt-1 text-sm text-muted-foreground">{subtitle}</p>}
          </div>
          <Badge variant={flight.status === 'Completed' ? 'success' : 'muted'}>{flight.status}</Badge>
        </CardHeader>
        <CardContent className="space-y-6">
          {hasLanding ? (
            <LandingGauge fpm={flight.landingFpmFirst!} />
          ) : (
            <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 p-3 text-sm text-muted-foreground">
              <Info className="size-4 shrink-0" />
              No touchdown was captured for this flight.
            </div>
          )}

          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <StatTile
              label="Peak G"
              icon={Gauge}
              value={flight.landingGForce !== null ? `${flight.landingGForce.toFixed(2)}g` : '—'}
            />
            <StatTile label="Bounces" icon={RotateCw} value={String(bounceCount)} />
            <StatTile
              label="Centreline deviation"
              icon={Target}
              value={flight.centrelineDeviationM !== null ? `${Math.round(flight.centrelineDeviationM)} m` : '—'}
            />
            <StatTile
              label="Hardest touchdown"
              icon={AlertTriangle}
              value={flight.landingFpmHardest !== null ? `${Math.round(-Math.abs(flight.landingFpmHardest))} fpm` : '—'}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Phase timeline</CardTitle>
        </CardHeader>
        <CardContent>
          <PhaseTimeline
            currentPhase={inferFinalPhase(flight)}
            ooooTimes={{ out: flight.outUtc, off: flight.offUtc, on: flight.onUtc, in: flight.inUtc }}
            completed
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Actual vs. planned</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-1.5 rounded-md border border-border p-3">
            <div className="flex items-center gap-2 text-sm font-medium">
              <Clock3 className="size-4 text-muted-foreground" />
              Block time
            </div>
            <div className="flex items-baseline gap-2 text-sm">
              <span className="font-mono text-lg font-semibold tabular-nums">
                {actualBlockMinutes !== null ? fmt.duration(actualBlockMinutes) : '—'}
              </span>
              <ArrowRight className="size-3.5 text-muted-foreground" />
              <span className="tabular-nums text-muted-foreground">{fmt.duration(flight.plannedBlockMinutes)} planned</span>
            </div>
            {actualBlockMinutes !== null && (
              <p className="text-xs">
                <DeltaLabel
                  actual={actualBlockMinutes}
                  planned={flight.plannedBlockMinutes}
                  unitLabel="min"
                  lowerIsBetter
                  betterLabel="ahead of schedule"
                  worseLabel="behind schedule"
                />
              </p>
            )}
          </div>
          <div className="space-y-1.5 rounded-md border border-border p-3">
            <div className="flex items-center gap-2 text-sm font-medium">
              <Fuel className="size-4 text-muted-foreground" />
              Fuel used
            </div>
            <div className="flex items-baseline gap-2 text-sm">
              <span className="font-mono text-lg font-semibold tabular-nums">{fmt.weight(flight.fuelUsedKg)}</span>
              <ArrowRight className="size-3.5 text-muted-foreground" />
              <span className="tabular-nums text-muted-foreground">{fmt.weight(flight.fuelPlannedKg)} planned</span>
            </div>
            <p className="text-xs">
              <DeltaLabel
                actual={flight.fuelUsedKg}
                planned={flight.fuelPlannedKg}
                unitLabel="kg"
                lowerIsBetter
                betterLabel="under plan"
                worseLabel="over plan"
              />
            </p>
          </div>
        </CardContent>
      </Card>

      {flight.typeMismatch && (
        <Card className="border-accent/30">
          <CardContent className="flex items-start gap-3 p-4">
            <Info className="mt-0.5 size-4 shrink-0 text-accent" />
            <div className="space-y-1 text-sm">
              <p className="font-medium">Aircraft type noted, not penalised</p>
              <p className="text-muted-foreground">
                {mismatchDetail
                  ? `You flew "${mismatchDetail.titleFlown || flight.titleFlown || 'an unrecognised aircraft'}" instead of the route's ${mismatchDetail.expectedFamily} (${mismatchDetail.expectedType}).`
                  : `You flew "${flight.titleFlown || 'an unrecognised aircraft'}" instead of this route's assigned aircraft type.`}{' '}
                This is informational only — <strong>it does not affect payment.</strong>
              </p>
            </div>
          </CardContent>
        </Card>
      )}

      <Separator />
      <p className="text-center text-xs text-muted-foreground">
        Revenue and costs for this flight aren&rsquo;t shown yet — the economy engine arrives in a later update.
      </p>
    </div>
  )
}
