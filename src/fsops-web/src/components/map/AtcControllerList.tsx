import { Radio, RadioTower, WifiOff } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import type { VatsimAtcController, VatsimAtcResponse } from '@/types/operations'
import type { VatsimAtcFetchStatus } from '@/hooks/useVatsimAtc'

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

interface AtcControllerListProps {
  status: VatsimAtcFetchStatus
  data: VatsimAtcResponse | null
  /** Highlights the row matching the currently-hovered map marker, keeping the list and the map
   *  readable as the same view from two angles instead of two disconnected UIs. */
  highlightedCallsign?: string | null
  onHoverController?: (callsign: string | null) => void
  className?: string
}

function ControllerRow({
  controller,
  highlighted,
  onHover,
}: {
  controller: VatsimAtcController
  highlighted: boolean
  onHover?: (callsign: string | null) => void
}) {
  return (
    <li
      className={cn(
        'flex items-center justify-between gap-3 rounded-md px-2 py-1.5 text-sm transition-colors',
        highlighted && 'bg-accent/10',
      )}
      onMouseEnter={() => onHover?.(controller.callsign)}
      onMouseLeave={() => onHover?.(null)}
    >
      <div className="flex min-w-0 items-center gap-2">
        <span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-accent/15 text-accent">
          <RadioTower className="size-3.5" />
        </span>
        <div className="min-w-0">
          <p className="truncate font-mono text-xs font-semibold tabular-nums">{controller.callsign}</p>
          <p className="truncate text-xs text-muted-foreground">
            {controller.airportName ?? controller.airportIcao ?? controller.facilityLabel}
            {controller.airportName ? ` · ${controller.facilityLabel}` : ''}
          </p>
        </div>
      </div>
      <div className="flex shrink-0 items-center gap-2 text-xs text-muted-foreground">
        <span className="hidden items-center gap-1 tabular-nums sm:flex">
          <Radio className="size-3 shrink-0" />
          {controller.frequency}
        </span>
        <span className="tabular-nums">{formatUtc(controller.logonTimeUtc)}</span>
      </div>
    </li>
  )
}

/**
 * The "listed somewhere readable" half of the ATC layer (docs/PLAN.md "VATSIM integration"):
 * every online controller covering an airport in the airline's own network, as a plain list
 * rather than relying solely on hovering a small map marker. Mirrors the same three states the
 * map layer shows (loading, unavailable, empty) so the two views never disagree.
 */
export function AtcControllerList({ status, data, highlightedCallsign, onHoverController, className }: AtcControllerListProps) {
  if (status === 'loading') {
    return (
      <div className={cn('space-y-2', className)}>
        <Skeleton className="h-9 w-full" />
        <Skeleton className="h-9 w-full" />
        <Skeleton className="h-9 w-3/4" />
      </div>
    )
  }

  const unavailable = status === 'error' || data?.status === 'unavailable'
  if (unavailable) {
    return (
      <div className={cn('flex items-center gap-2 rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground', className)}>
        <WifiOff className="size-4 shrink-0" />
        ATC data unavailable right now — the map and your flight are unaffected.
      </div>
    )
  }

  const controllers = data?.controllers ?? []
  if (controllers.length === 0) {
    return (
      <div className={cn('flex items-center gap-2 rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground', className)}>
        <RadioTower className="size-4 shrink-0" />
        No controllers online near your network right now.
      </div>
    )
  }

  return (
    <div className={className}>
      <ul className="max-h-64 space-y-0.5 overflow-y-auto">
        {controllers.map((controller) => (
          <ControllerRow
            key={controller.callsign}
            controller={controller}
            highlighted={highlightedCallsign === controller.callsign}
            onHover={onHoverController}
          />
        ))}
      </ul>
    </div>
  )
}

/** Small badge for a card header - "3 online" / nothing when there's nothing to report, matching
 *  the "X airborne" badge already used next to the live operations map title. */
export function AtcCountBadge({ status, data }: { status: VatsimAtcFetchStatus; data: VatsimAtcResponse | null }) {
  if (status !== 'ready' || data?.status !== 'ok' || data.controllers.length === 0) return null
  return <Badge variant="success">{data.controllers.length} online</Badge>
}
