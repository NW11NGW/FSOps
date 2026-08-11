import { useLayoutEffect, useRef, useState } from 'react'
import { Clock3, Radio } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import type { VatsimAtcController } from '@/types/operations'

const TIME_FORMATTER = new Intl.DateTimeFormat('en-US', {
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'UTC',
})

function formatUtc(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '—' : `${TIME_FORMATTER.format(date)}Z`
}

interface AtcControllerCardProps {
  controller: VatsimAtcController
  /** Viewport-relative pixel coordinates of the controller marker, same convention as
   *  LiveFlightCard - this renders `position: fixed` so it escapes the map's own
   *  `overflow-hidden` container instead of clipping near an edge. */
  x: number
  y: number
}

/**
 * Hover card for an online controller: callsign, facility, frequency, the airport it covers, and
 * how long they've been logged on. Deliberately much shorter than LiveFlightCard - there is no
 * route, progress, or phase to show for a controller, only who they are and where.
 */
export function AtcControllerCard({ controller, x, y }: AtcControllerCardProps) {
  const cardRef = useRef<HTMLDivElement>(null)
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

  return (
    <div
      ref={cardRef}
      className="pointer-events-none fixed z-50 w-64 rounded-lg border border-border bg-surface-elevated p-3 shadow-elevation-3"
      style={{ left: style.left, top: style.top }}
      role="status"
    >
      <div className="flex items-start justify-between gap-2">
        <p className="min-w-0 break-words font-mono text-sm font-semibold tabular-nums">{controller.callsign}</p>
        <Badge variant="outline" className="shrink-0">
          {controller.facilityLabel}
        </Badge>
      </div>

      {controller.airportName && (
        <p className="mt-0.5 text-xs text-muted-foreground">
          {controller.airportName}
          {controller.airportIcao ? ` (${controller.airportIcao})` : ''}
        </p>
      )}

      <div className="mt-2.5 space-y-1.5 text-xs">
        <div className="flex items-center gap-1.5 text-foreground/90">
          <Radio className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="tabular-nums">{controller.frequency}</span>
        </div>
        <div className="flex items-center gap-1.5 text-foreground/90">
          <Clock3 className="size-3.5 shrink-0 text-muted-foreground" />
          <span>Online since {formatUtc(controller.logonTimeUtc)}</span>
        </div>
      </div>
    </div>
  )
}
