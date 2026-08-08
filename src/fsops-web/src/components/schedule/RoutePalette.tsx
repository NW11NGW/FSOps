import type { DragEvent } from 'react'
import { GripVertical, Route as RouteIcon } from 'lucide-react'

import type { RouteLike } from './types'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'

export const ROUTE_DRAG_TYPE = 'text/x-fsops-route-id'
export const ENTRY_DRAG_TYPE = 'text/x-fsops-entry-id'

interface RoutePaletteProps {
  routes: RouteLike[]
  status: 'loading' | 'ready' | 'error'
  /** Both interactions land in the same place: dragging sets up an HTML5 drag payload for
   *  dropping on the grid; clicking is the keyboard- and mouse-accessible equivalent that opens
   *  the same add-leg dialog with this route pre-selected. Neither bypasses the legality check -
   *  they only choose which route the dialog starts with. */
  onSelectRoute: (routeId: string) => void
}

/** The source list of routes a leg can be built from. Chips are draggable onto the calendar; they
 *  are also plain buttons so choosing one never requires a mouse. */
export function RoutePalette({ routes, status, onSelectRoute }: RoutePaletteProps) {
  const { fmt } = useSettings()

  function handleDragStart(event: DragEvent<HTMLButtonElement>, routeId: string) {
    event.dataTransfer.setData(ROUTE_DRAG_TYPE, routeId)
    event.dataTransfer.effectAllowed = 'copy'
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Routes</CardTitle>
        <p className="text-sm text-muted-foreground">
          Drag a route onto the grid, or click one to place it by day and time.
        </p>
      </CardHeader>
      <CardContent className="max-h-80 space-y-2 overflow-y-auto">
        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">Could not load routes.</p>}

        {status === 'ready' && routes.length === 0 && (
          <p className="rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground">
            No routes yet - create one on the Routes page first.
          </p>
        )}

        {status === 'ready' &&
          routes.map((route) => (
            <button
              key={route.id}
              type="button"
              draggable
              onDragStart={(event) => handleDragStart(event, route.id)}
              onClick={() => onSelectRoute(route.id)}
              className="flex w-full cursor-grab items-center gap-2 rounded-md border border-border bg-surface p-2.5 text-left text-sm shadow-elevation-1 transition-colors hover:border-ring hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring active:cursor-grabbing"
            >
              <GripVertical className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <RouteIcon className="size-4 shrink-0 text-accent" aria-hidden="true" />
              <span className="min-w-0 flex-1 break-words">
                <span className="font-mono font-medium">
                  {route.departureIcao} → {route.arrivalIcao}
                </span>
                {route.flightNumber && <span className="ml-2 text-muted-foreground">{route.flightNumber}</span>}
                {route.estimatedBlockMinutes != null && (
                  <span className="block text-xs text-muted-foreground">{fmt.duration(route.estimatedBlockMinutes)} block</span>
                )}
              </span>
            </button>
          ))}
      </CardContent>
    </Card>
  )
}
