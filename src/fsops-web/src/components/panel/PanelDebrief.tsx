import { AlertTriangle, Banknote, Clock3, Gauge } from 'lucide-react'

import { MaintenanceWarningLine } from '@/components/panel/MaintenanceWarningLine'
import { Badge } from '@/components/ui/badge'
import { useSettings } from '@/hooks/useSettings'
import { formatCallsign } from '@/lib/callsign'
import { assessLanding, minutesBetween } from '@/lib/flightFormat'
import type { MaintenanceWarning } from '@/lib/panelFormat'
import { blockVarianceMinutes, netEarned } from '@/lib/panelFormat'
import { cn } from '@/lib/utils'
import type { Flight } from '@/types/flight'
import type { RouteSummary } from '@/types/route'

interface PanelDebriefProps {
  flight: Flight
  route: RouteSummary | null
  airlineIcaoCode: string | null
  maintenanceWarning: MaintenanceWarning | null
}

const VERDICT_COLOR: Record<ReturnType<typeof assessLanding>['verdict'], string> = {
  smooth: 'text-success',
  firm: 'text-warning',
  hard: 'text-danger',
}

/** The on-the-ground job: how the landing went, whether the sector ran to plan, and what it
 *  earned - the same figures the full report card shows, sourced from the flight row itself so
 *  this never needs to fetch the ledger. */
export function PanelDebrief({ flight, route, airlineIcaoCode, maintenanceWarning }: PanelDebriefProps) {
  const { fmt } = useSettings()

  const callsign = formatCallsign(airlineIcaoCode, route?.flightNumber ?? null)
  const routeLabel = route ? `${route.departureIcao} → ${route.arrivalIcao}` : 'Flight complete'
  const notPayable = flight.slewDetected || flight.positionJumpDetected

  const actualBlockMinutes = minutesBetween(flight.outUtc, flight.inUtc)
  const variance = actualBlockMinutes !== null ? blockVarianceMinutes(actualBlockMinutes, flight.plannedBlockMinutes) : null

  const landing = flight.landingFpmFirst !== null ? assessLanding(flight.landingFpmFirst) : null
  const net = netEarned(flight)

  return (
    <div className="space-y-3">
      <div className="space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="font-mono text-2xl font-bold tracking-tight">{routeLabel}</h1>
          {callsign && (
            <Badge variant="outline" className="font-mono text-sm">
              {callsign}
            </Badge>
          )}
        </div>
        <Badge variant="success" className="text-xs">
          Landed
        </Badge>
      </div>

      <div className="rounded-md border border-border bg-surface p-3">
        <p className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Touchdown rate</p>
        {landing ? (
          <div className="flex items-baseline justify-between gap-2">
            <p className="font-mono text-3xl font-bold tabular-nums tracking-tight">
              {Math.round(landing.displayFpm)} <span className="text-sm font-medium text-muted-foreground">fpm</span>
            </p>
            <p className={cn('text-sm font-semibold', VERDICT_COLOR[landing.verdict])}>{landing.label}</p>
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">No touchdown was captured.</p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-2">
        <div className="flex items-center gap-2.5 rounded-md border border-border bg-surface px-3 py-2.5">
          <Clock3 className="size-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0">
            <p className="truncate text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Block variance</p>
            <p className="truncate font-mono text-lg font-semibold tabular-nums leading-tight">
              {variance === null
                ? '—'
                : Math.abs(variance) < 0.5
                  ? 'On plan'
                  : `${variance > 0 ? '+' : ''}${Math.round(variance)}m`}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2.5 rounded-md border border-border bg-surface px-3 py-2.5">
          <Banknote className="size-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0">
            <p className="truncate text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Sector earned</p>
            <p
              className={cn(
                'truncate font-mono text-lg font-semibold tabular-nums leading-tight',
                net >= 0 ? 'text-success' : 'text-danger',
              )}
            >
              {net < 0 ? '-' : ''}
              {fmt.money(Math.abs(net))}
            </p>
          </div>
        </div>
      </div>

      {notPayable && (
        <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-3 py-2 text-xs text-warning">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
          This sector wasn&rsquo;t valid for payment (slew or a position jump was detected).
        </div>
      )}

      {flight.simRateElevated && (
        <div className="flex items-start gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
          <Gauge className="mt-0.5 size-3.5 shrink-0" />
          Time acceleration was used - block time above is not a measured figure.
        </div>
      )}

      {maintenanceWarning && <MaintenanceWarningLine warning={maintenanceWarning} />}
    </div>
  )
}
