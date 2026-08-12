import { Radio, RadioTower, Spline, WifiOff } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import type { VatsimAtcController, VatsimAtcResponse } from '@/types/operations'
import type { VatsimAtcFetchStatus } from '@/hooks/useVatsimAtc'
import {
  filterControllersToViewport,
  sortControllersForDisplay,
  type MapBounds,
} from '@/components/map/atcVisibility'

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
  /** The map's current bounds. When set, the list shows only what is genuinely visible there, so
   *  the two views can never disagree while the user is looking at both. Null when there is no map
   *  at all (the in-game panel) or before it has mounted, in which case the server's own
   *  network scoping stands. */
  viewport?: MapBounds | null
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
  const isSector = controller.coverageKind === 'sector'
  const secondaryLine = isSector
    ? (controller.boundaryName ?? controller.facilityLabel)
    : (controller.airportName ?? controller.airportIcao ?? controller.facilityLabel)

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
        {/* Filled for a position covering one of the airline's own airports, outlined otherwise.
            Network relevance stopped being a filter when the list started following the map, so
            it has to carry its weight visually instead. */}
        <span
          className={cn(
            'flex size-6 shrink-0 items-center justify-center rounded-full',
            controller.inNetwork
              ? 'bg-accent/15 text-accent'
              : 'border border-border bg-transparent text-muted-foreground',
          )}
        >
          {isSector ? <Spline className="size-3.5" /> : <RadioTower className="size-3.5" />}
        </span>
        <div className="min-w-0">
          <p className="truncate font-mono text-xs font-semibold tabular-nums">{controller.callsign}</p>
          <p className="truncate text-xs text-muted-foreground">
            {secondaryLine}
            {secondaryLine !== controller.facilityLabel ? ` · ${controller.facilityLabel}` : ''}
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

function EmptyState({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <div
      className={cn(
        'flex items-center gap-2 rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground',
        className,
      )}
    >
      <RadioTower className="size-4 shrink-0" />
      {children}
    </div>
  )
}

/**
 * The "listed somewhere readable" half of the ATC layer: the
 * online controllers the user can actually see, as a plain list rather than relying solely on
 * hovering a small map marker.
 *
 * <b>The list follows the map.</b> Looking at the UK should not produce frequencies for the UAE.
 * Because the user takes in both views at once, a list naming a sector nowhere near the screen -
 * or omitting one that is drawn on it - reads as a bug in the map rather than a scoping choice. So
 * the filter is the viewport, and it is applied to data already in hand: panning re-filters
 * instantly and never touches the VATSIM feed.
 *
 * Mirrors the same states the map layer shows (loading, unavailable, empty) so the two views never
 * disagree, and distinguishes an empty *area* from an empty *network* - a user staring at an empty
 * list needs to know whether to zoom out or to conclude nobody is online.
 */
export function AtcControllerList({
  status,
  data,
  viewport = null,
  highlightedCallsign,
  onHoverController,
  className,
}: AtcControllerListProps) {
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

  const all = data?.controllers ?? []
  const visible = sortControllersForDisplay(
    filterControllersToViewport(all, data?.boundaries ?? null, viewport),
  )

  if (visible.length === 0) {
    // Three genuinely different situations, and telling them apart is the whole point: nobody is
    // online anywhere, nobody is online *here*, or there is no map to be "here" on.
    if (viewport && all.length > 0) {
      return (
        <EmptyState className={className}>
          No controllers online in this part of the map — try zooming out or panning.
        </EmptyState>
      )
    }
    return (
      <EmptyState className={className}>
        {viewport
          ? 'No controllers online in this area right now.'
          : 'No controllers online near your network right now.'}
      </EmptyState>
    )
  }

  return (
    <div className={className}>
      <ul className="max-h-64 space-y-0.5 overflow-y-auto">
        {visible.map((controller) => (
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

/** Small badge for a card header - "3 in view" / nothing when there's nothing to report, matching
 *  the "X airborne" badge already used next to the live operations map title. Counts what the list
 *  is actually showing, so the badge, the list and the map all agree. */
export function AtcCountBadge({
  status,
  data,
  viewport = null,
}: {
  status: VatsimAtcFetchStatus
  data: VatsimAtcResponse | null
  viewport?: MapBounds | null
}) {
  if (status !== 'ready' || data?.status !== 'ok') return null
  const count = filterControllersToViewport(data.controllers, data.boundaries, viewport).length
  if (count === 0) return null
  return <Badge variant="success">{viewport ? `${count} in view` : `${count} online`}</Badge>
}
