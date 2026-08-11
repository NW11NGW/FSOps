import { AlertTriangle, ArrowRight, Banknote, Clock3, Fuel, Gauge, Info, Minus, Plus, RotateCw, Target } from 'lucide-react'

import { LandingGauge } from '@/components/flight/LandingGauge'
import { PhaseTimeline } from '@/components/flight/PhaseTimeline'
import { StatTile } from '@/components/shared/StatTile'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { useSettings } from '@/hooks/useSettings'
import { formatCallsign } from '@/lib/callsign'
import { minutesBetween } from '@/lib/flightFormat'
import { cn } from '@/lib/utils'
import type { FlightDetail, FlightPhase, MismatchPayload } from '@/types/flight'

interface ReportRouteInfo {
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  flightNumber: string | null
}

interface ReportCardProps {
  detail: FlightDetail
  route: ReportRouteInfo | null
  airlineIcaoCode: string | null
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
 * actual-vs-planned, an informational (never punitive) aircraft-type badge, and finally the
 * itemised financial outcome - straight from the flight's posted LedgerTransaction rows, never a
 * recomputation, so it can never show a figure that doesn't match what actually moved the
 * airline's cash balance.
 */
export function ReportCard({ detail, route, airlineIcaoCode, className }: ReportCardProps) {
  const { flight } = detail
  const { fmt } = useSettings()

  // Default the collections rather than destructuring them raw. A response that predates a field -
  // an older server, a partial payload, a flight recorded before a feature existed - would
  // otherwise throw inside render and, with no boundary above, blank the entire app rather than
  // just this card. Missing history is a rendering detail, not a fatal condition.
  const events = detail.events ?? []
  const ledgerTransactions = detail.ledgerTransactions ?? []

  const netTotal = ledgerTransactions.reduce((sum, line) => sum + line.amount, 0)

  // Fuel is charged on uplift, never on burn (see docs/PLAN.md "Persistent fuel state and
  // tankering"), so this can legitimately be zero - a return leg flown on fuel already in the
  // tank posts no Fuel ledger line at all, which is the headline property of that model made
  // visible right here.
  const fuelCostThisFlight = ledgerTransactions
    .filter((line) => line.category === 'Fuel')
    .reduce((sum, line) => sum + line.amount, 0)
  const fuelWasUplifted = ledgerTransactions.some((line) => line.category === 'Fuel')
  const sectorNotPayable = flight.slewDetected || flight.positionJumpDetected

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
  const callsign = route ? formatCallsign(airlineIcaoCode, route.flightNumber) : null

  return (
    <div className={cn('space-y-4', className)}>
      <Card>
        <CardHeader className="flex-row items-start justify-between gap-4 space-y-0">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <CardTitle className="font-mono text-xl">{title}</CardTitle>
              {callsign && (
                <Badge variant="outline" className="font-mono">
                  {callsign}
                </Badge>
              )}
            </div>
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

          <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-3">
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

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Fuel className="size-4 text-muted-foreground" />
            Fuel
          </CardTitle>
        </CardHeader>
        <CardContent className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-3">
          <StatTile label="Burned this flight" icon={Fuel} value={fmt.weight(flight.fuelUsedKg)} />
          <StatTile
            label="Fuel cost this flight"
            icon={Banknote}
            value={fuelWasUplifted ? fmt.money(Math.abs(fuelCostThisFlight)) : fmt.money(0)}
          />
          <StatTile
            label="Remaining on the aircraft"
            icon={Gauge}
            value={detail.aircraftFuelOnBoardKg !== null ? fmt.weight(detail.aircraftFuelOnBoardKg) : '—'}
          />
        </CardContent>
        {!fuelWasUplifted && (
          <CardContent className="pt-0">
            <p className="text-xs text-muted-foreground">
              No fuel was bought for this flight — it flew on fuel already on board from an earlier flight.
            </p>
          </CardContent>
        )}
      </Card>

      {(flight.simRateElevated || flight.slewDetected || flight.positionJumpDetected) && (
        <Card className={flight.slewDetected || flight.positionJumpDetected ? 'border-warning/30' : undefined}>
          <CardHeader>
            <CardTitle className="text-base">Flight integrity</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {flight.simRateElevated && (
              <div className="flex items-start gap-3 text-sm">
                <Gauge className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                <p className="min-w-0 break-words text-muted-foreground">
                  Time acceleration was used during this flight
                  {flight.maxSimulationRateObserved > 1
                    ? ` (up to ${flight.maxSimulationRateObserved.toFixed(1)}x)`
                    : ''}
                  . Block time and on-time performance above are{' '}
                  <strong className="text-foreground">not measured</strong> as a result — elapsed wall time
                  doesn&rsquo;t mean anything once the sim clock runs faster than real time. Landing quality is
                  unaffected.
                </p>
              </div>
            )}
            {(flight.slewDetected || flight.positionJumpDetected) && (
              <div className="flex items-start gap-3 text-sm">
                <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" />
                <p className="min-w-0 break-words text-warning">
                  {flight.slewDetected && flight.positionJumpDetected
                    ? 'Slew was active and telemetry showed a position jump during this flight.'
                    : flight.slewDetected
                      ? 'Slew was active during this flight.'
                      : 'Telemetry showed a position change inconsistent with normal flight.'}{' '}
                  This sector is not valid for payment.
                </p>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {flight.typeMismatch === true && (
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

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Banknote className="size-4 text-muted-foreground" />
            Financial outcome
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          {ledgerTransactions.length === 0 ? (
            <p className="text-sm text-muted-foreground">No financial lines were posted for this flight.</p>
          ) : (
            <>
              <ul className="space-y-2">
                {ledgerTransactions.map((line) => {
                  const isCredit = line.amount >= 0
                  return (
                    <li key={line.id} className="flex items-start justify-between gap-3 text-sm">
                      <div className="flex min-w-0 items-start gap-2">
                        {isCredit ? (
                          <Plus className="mt-0.5 size-3.5 shrink-0 text-success" />
                        ) : (
                          <Minus className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
                        )}
                        <span className="min-w-0 break-words text-foreground">{line.description}</span>
                      </div>
                      <span className={cn('shrink-0 tabular-nums font-medium', isCredit ? 'text-success' : 'text-foreground')}>
                        {isCredit ? '' : '-'}
                        {fmt.money(Math.abs(line.amount))}
                      </span>
                    </li>
                  )
                })}
              </ul>
              <Separator />
              <div className="flex items-center justify-between text-sm font-semibold">
                <span>Net</span>
                <span className={cn('tabular-nums', netTotal >= 0 ? 'text-success' : 'text-danger')}>
                  {netTotal < 0 ? '-' : ''}
                  {fmt.money(Math.abs(netTotal))}
                </span>
              </div>
              {sectorNotPayable && (
                <p className="text-xs text-muted-foreground">
                  This sector wasn&rsquo;t valid for payment (see flight integrity above) — only fuel already bought stays
                  charged, and no ticket revenue was posted.
                </p>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
