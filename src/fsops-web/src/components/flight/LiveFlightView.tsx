import { useState } from 'react'
import { Compass, Fuel, Gauge, MoveVertical, Navigation, WifiOff, X } from 'lucide-react'

import { AbandonDialog } from '@/components/flight/AbandonDialog'
import { LiveFlightMap } from '@/components/flight/LiveFlightMap'
import { PhaseTimeline } from '@/components/flight/PhaseTimeline'
import { StatTile } from '@/components/shared/StatTile'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { Flight, LiveFlightSnapshot, TelemetryPayload } from '@/types/flight'
import type { LonLat } from '@/types/route'

interface LiveAirportPoint {
  icao: string
  latitude: number
  longitude: number
}

interface LiveFlightViewProps {
  flight: Flight
  live: LiveFlightSnapshot | null
  telemetry: TelemetryPayload | null
  hubConnected: boolean
  departure: LiveAirportPoint | null
  arrival: LiveAirportPoint | null
  path: LonLat[]
  routeLabel: string
  onAbandon: () => Promise<void>
}

/** In-progress flight: phase, live map, readouts, elapsed vs planned block time, abandon action. */
export function LiveFlightView({ flight, live, telemetry, hubConnected, departure, arrival, path, routeLabel, onAbandon }: LiveFlightViewProps) {
  const { fmt } = useSettings()
  const [abandonOpen, setAbandonOpen] = useState(false)

  const paused = !hubConnected || Boolean(live?.awaitingSimReconnect)
  const currentPhase = live?.phase ?? 'Preflight'

  const elapsed = live?.elapsedBlockMinutes ?? 0
  const planned = live?.plannedBlockMinutes ?? flight.plannedBlockMinutes
  const progressPercent = planned > 0 ? Math.min(100, (elapsed / planned) * 100) : 0
  const deltaMinutes = elapsed - planned
  const aheadOfSchedule = deltaMinutes < 0

  const aircraftPoint =
    live && telemetry
      ? { latitude: live.latitudeDeg, longitude: live.longitudeDeg, headingDeg: telemetry.headingTrue }
      : live
        ? { latitude: live.latitudeDeg, longitude: live.longitudeDeg, headingDeg: 0 }
        : null

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader className="flex-row items-center justify-between gap-4 space-y-0">
          <div>
            <CardTitle className="font-mono text-lg">{routeLabel}</CardTitle>
            <p className="mt-0.5 text-sm text-muted-foreground">Flight in progress</p>
          </div>
          <Button type="button" variant="outline" size="sm" className="gap-1.5 text-danger hover:text-danger" onClick={() => setAbandonOpen(true)}>
            <X className="size-3.5" />
            Abandon flight
          </Button>
        </CardHeader>
        <CardContent className="space-y-4">
          {paused && (
            <div className="flex items-center gap-2 rounded-md border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
              <WifiOff className="size-4 shrink-0" />
              Tracking paused — waiting for the simulator to reconnect. Nothing is lost; this will pick back up automatically.
            </div>
          )}
          <PhaseTimeline currentPhase={currentPhase} />
        </CardContent>
      </Card>

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_320px]">
        <LiveFlightMap
          departure={departure}
          arrival={arrival}
          path={path}
          aircraft={aircraftPoint}
          className="h-[320px] sm:h-[420px] lg:h-[480px]"
        />

        <div className="space-y-4">
          <Card>
            <CardHeader className="pb-2">
              <CardTitle className="text-sm font-medium text-muted-foreground">Block time</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2">
              <p className="font-mono text-2xl font-semibold tabular-nums">{fmt.duration(elapsed)}</p>
              <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted">
                <div
                  className={cn('h-full rounded-full transition-[width]', deltaMinutes > 5 ? 'bg-warning' : 'bg-accent')}
                  style={{ width: `${progressPercent}%` }}
                />
              </div>
              <p className={cn('text-xs font-medium', aheadOfSchedule ? 'text-success' : Math.abs(deltaMinutes) < 2 ? 'text-muted-foreground' : 'text-warning')}>
                {Math.abs(deltaMinutes) < 2
                  ? 'On schedule'
                  : `${Math.round(Math.abs(deltaMinutes))} min ${aheadOfSchedule ? 'ahead of' : 'behind'} plan`}{' '}
                <span className="text-muted-foreground">· {fmt.duration(planned)} planned</span>
              </p>
            </CardContent>
          </Card>

          <div className="grid grid-cols-2 gap-3">
            <StatTile label="Altitude" icon={Gauge} value={live ? fmt.altitude(live.altitudeMslFt) : undefined} />
            <StatTile label="IAS" icon={Navigation} value={live ? `${Math.round(live.indicatedAirspeedKt)} kt` : undefined} />
            <StatTile label="Ground speed" icon={Navigation} value={live ? `${Math.round(live.groundSpeedKt)} kt` : undefined} />
            <StatTile label="Vertical speed" icon={MoveVertical} value={live ? `${Math.round(live.verticalSpeedFpm)} fpm` : undefined} />
            <StatTile label="Heading" icon={Compass} value={telemetry ? `${Math.round(telemetry.headingTrue)}°` : undefined} />
            <StatTile label="Fuel remaining" icon={Fuel} value={live ? fmt.weight(live.fuelRemainingKg) : undefined} />
          </div>

          {!hubConnected && (
            <Badge variant="muted" className="w-fit">
              Live connection reconnecting…
            </Badge>
          )}
        </div>
      </div>

      <AbandonDialog open={abandonOpen} onOpenChange={setAbandonOpen} onConfirm={onAbandon} />
    </div>
  )
}
