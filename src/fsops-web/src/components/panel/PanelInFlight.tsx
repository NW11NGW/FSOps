import { Banknote, Clock3, Fuel, Users, WifiOff } from 'lucide-react'

import { MaintenanceWarningLine } from '@/components/panel/MaintenanceWarningLine'
import { NextFlightsList } from '@/components/panel/NextFlightsList'
import { PanelStat } from '@/components/panel/PanelStat'
import { Badge } from '@/components/ui/badge'
import { useSettings } from '@/hooks/useSettings'
import { formatCallsign } from '@/lib/callsign'
import { formatClockTime, PHASE_LABELS } from '@/lib/flightFormat'
import type { MaintenanceWarning } from '@/lib/panelFormat'
import { progressPercent, projectEtaIso, remainingBlockMinutes } from '@/lib/panelFormat'
import { cn } from '@/lib/utils'
import type { Flight, FlightOption, LiveFlightSnapshot } from '@/types/flight'
import type { RouteSummary } from '@/types/route'

interface PanelInFlightProps {
  flight: Flight
  live: LiveFlightSnapshot | null
  route: RouteSummary | null
  airlineIcaoCode: string | null
  cashBalance: number | null
  hubConnected: boolean
  nextFlights: FlightOption[]
  maintenanceWarning: MaintenanceWarning | null
}

/** The in-air job: flight number/callsign, departure/arrival, phase, progress, ETA, block time
 *  remaining, fuel, passengers, cash, and what's next - everything the plan calls for, nothing
 *  that needs a map or a chart. */
export function PanelInFlight({
  flight,
  live,
  route,
  airlineIcaoCode,
  cashBalance,
  hubConnected,
  nextFlights,
  maintenanceWarning,
}: PanelInFlightProps) {
  const { fmt, settings } = useSettings()

  const callsign = formatCallsign(airlineIcaoCode, route?.flightNumber ?? null)
  const routeLabel = route ? `${route.departureIcao} → ${route.arrivalIcao}` : 'Flight in progress'
  const phase = live?.phase ?? 'Preflight'
  const paused = !hubConnected || Boolean(live?.awaitingSimReconnect)

  const elapsed = live?.elapsedBlockMinutes ?? 0
  const planned = live?.plannedBlockMinutes ?? flight.plannedBlockMinutes
  const remaining = remainingBlockMinutes(elapsed, planned)
  const percent = progressPercent(elapsed, planned)
  const eta = live ? projectEtaIso(live.timestampUtc, remaining) : null

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
        <Badge variant="default" className="text-xs">
          {PHASE_LABELS[phase]}
        </Badge>
      </div>

      {paused && (
        <div className="flex items-center gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-xs text-warning">
          <WifiOff className="size-3.5 shrink-0" />
          Tracking paused — waiting for the simulator to reconnect.
        </div>
      )}

      <div className="rounded-md border border-border bg-surface p-3">
        <p className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Block time remaining</p>
        <p className="font-mono text-3xl font-bold tabular-nums tracking-tight">{fmt.duration(remaining)}</p>
        <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-muted">
          <div className="h-full rounded-full bg-accent transition-[width]" style={{ width: `${percent}%` }} />
        </div>
        <p className="mt-1.5 text-xs text-muted-foreground">
          ETA {eta ? formatClockTime(eta, settings.timeDisplay === 'Utc', settings.use24HourClock) : '—'} ·{' '}
          {Math.round(percent)}% complete
        </p>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <PanelStat label="Fuel remaining" icon={Fuel} value={live ? fmt.weight(live.fuelRemainingKg) : '—'} />
        <PanelStat label="Passengers" icon={Users} value={String(flight.paxBooked)} />
        <PanelStat label="Cash" icon={Banknote} value={cashBalance !== null ? fmt.money(cashBalance) : '—'} />
        <PanelStat label="Planned block" icon={Clock3} value={fmt.duration(planned)} />
      </div>

      {maintenanceWarning && <MaintenanceWarningLine warning={maintenanceWarning} />}

      {nextFlights.length > 0 && (
        <div className={cn('space-y-1.5 border-t border-border pt-3')}>
          <p className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Next up</p>
          <NextFlightsList flights={nextFlights} airlineIcaoCode={airlineIcaoCode} />
        </div>
      )}
    </div>
  )
}
