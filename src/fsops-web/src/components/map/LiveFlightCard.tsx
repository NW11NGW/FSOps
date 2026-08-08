import { useLayoutEffect, useRef, useState } from 'react'
import { ArrowRight, Clock3, Gauge, PlaneTakeoff, User } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'
import type { LiveAircraft } from '@/types/operations'

const TIME_FORMATTER = new Intl.DateTimeFormat('en-US', {
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
})

/** "TaxiOut" -> "Taxi Out" - generic so it never needs a lookup table kept in sync with every
 *  phase name the flight-phase state machine or the live-ops endpoint's own phase mapper emit. */
function humanizePhase(phase: string): string {
  return phase.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}

function formatMinutes(minutes: number): string {
  const total = Math.max(0, Math.round(minutes))
  const hours = Math.floor(total / 60)
  const mins = total % 60
  return hours === 0 ? `${mins}m` : `${hours}h ${mins}m`
}

function formatUtc(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '—' : `${TIME_FORMATTER.format(date)}Z`
}

interface LiveFlightCardProps {
  aircraft: LiveAircraft
  /** Viewport-relative pixel coordinates of the aircraft marker (see LiveOpsMap's
   *  updateHoverPosition) - this card renders `position: fixed` so it escapes the map's own
   *  `overflow-hidden` container instead of being clipped near an edge. */
  x: number
  y: number
}

/**
 * The hover flight card from docs/PLAN.md "Live operations map": flight number, route, pilot,
 * registration and type, departure/ETA, elapsed vs remaining, percent complete, current phase.
 * `aircraftType` is left out of the layout when absent (see types/operations.ts) rather than
 * showing a blank or "undefined" - a flight whose FleetAircraft record didn't resolve still has
 * a valid registration/route to show.
 */
export function LiveFlightCard({ aircraft, x, y }: LiveFlightCardProps) {
  const cardRef = useRef<HTMLDivElement>(null)
  // Clamp into the viewport after layout - a fixed offset above the marker overflows near the
  // top/side edges otherwise, which is exactly where a marker near the edge of a wide network is
  // likely to sit.
  const [style, setStyle] = useState<{ left: number; top: number }>({ left: x, top: y })

  useLayoutEffect(() => {
    const el = cardRef.current
    if (!el) {
      setStyle({ left: x, top: y })
      return
    }
    const { width, height } = el.getBoundingClientRect()
    const margin = 8
    let left = x - width / 2
    let top = y - height - 16
    left = Math.min(Math.max(left, margin), window.innerWidth - width - margin)
    if (top < margin) top = y + 16 // flip below the marker if there's no room above
    top = Math.min(top, window.innerHeight - height - margin)
    setStyle({ left, top })
  }, [x, y])

  const route =
    aircraft.departureIcao && aircraft.arrivalIcao ? `${aircraft.departureIcao} → ${aircraft.arrivalIcao}` : null

  return (
    <div
      ref={cardRef}
      className="pointer-events-none fixed z-50 w-72 rounded-lg border border-border bg-surface-elevated p-3 shadow-elevation-3"
      style={{ left: style.left, top: style.top }}
      role="status"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="min-w-0 break-words text-sm font-semibold tabular-nums">
            {aircraft.flightNumber ?? 'Flight'}
          </p>
          {route && (
            <p className="mt-0.5 flex min-w-0 items-center gap-1 text-xs text-muted-foreground">
              <span className="min-w-0 break-words">{route}</span>
            </p>
          )}
        </div>
        <Badge variant={aircraft.kind === 'player' ? 'default' : 'secondary'} className="shrink-0">
          {aircraft.kind === 'player' ? 'You' : 'Virtual'}
        </Badge>
      </div>

      <div className="mt-2.5 space-y-1.5 text-xs">
        <div className="flex items-center gap-1.5 text-foreground/90">
          <User className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 break-words">{aircraft.pilotName}</span>
        </div>
        <div className="flex items-center gap-1.5 text-foreground/90">
          <PlaneTakeoff className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 break-words">
            {aircraft.registration ?? '—'}
            {aircraft.aircraftType ? ` · ${aircraft.aircraftType}` : ''}
          </span>
        </div>
        <div className="flex items-center gap-1.5 text-foreground/90">
          <Clock3 className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 break-words">
            {formatUtc(aircraft.departureUtc)}
            <ArrowRight className="mx-1 inline size-3 shrink-0 align-[-1px] text-muted-foreground" />
            {formatUtc(aircraft.estimatedArrivalUtc)}
          </span>
        </div>
        <div className="flex items-center gap-1.5 text-foreground/90">
          <Gauge className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 break-words">
            {formatMinutes(aircraft.elapsedMinutes)} elapsed · {formatMinutes(aircraft.remainingMinutes)} remaining
          </span>
        </div>
      </div>

      <div className="mt-2.5 flex items-center justify-between gap-2">
        <Badge variant="outline">{humanizePhase(aircraft.phase)}</Badge>
        <span className="text-xs font-medium tabular-nums text-muted-foreground">
          {aircraft.percentComplete !== null ? `${Math.round(aircraft.percentComplete)}%` : '—'}
        </span>
      </div>

      {aircraft.percentComplete !== null && (
        <div className={cn('mt-1.5 h-1 overflow-hidden rounded-full bg-muted')}>
          <div
            className="h-full rounded-full bg-accent transition-[width]"
            style={{ width: `${Math.min(100, Math.max(0, aircraft.percentComplete))}%` }}
          />
        </div>
      )}
    </div>
  )
}
