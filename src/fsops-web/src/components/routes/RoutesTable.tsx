import { useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'
import { Route as RouteIcon, Trash2 } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import type { RoutesStatus } from '@/hooks/useRoutes'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { RouteSummary } from '@/types/route'

interface RoutesTableProps {
  routes: RouteSummary[]
  status: RoutesStatus
  blockMinutes: Record<string, number | undefined>
  selectedId: string | null
  hoveredId?: string | null
  onSelect: (route: RouteSummary) => void
  onDelete: (route: RouteSummary) => Promise<void>
  onHover?: (routeId: string | null) => void
}

/** The airline's saved routes: distance/fare from the list endpoint, block time backfilled per-row via preview. */
export function RoutesTable({ routes, status, blockMinutes, selectedId, hoveredId, onSelect, onDelete, onHover }: RoutesTableProps) {
  const { fmt } = useSettings()
  const [deletingRoute, setDeletingRoute] = useState<RouteSummary | null>(null)
  const [isDeleting, setIsDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  function requestDelete(event: MouseEvent, route: RouteSummary) {
    event.stopPropagation()
    setDeleteError(null)
    setDeletingRoute(route)
  }

  function stopRowActivation(event: KeyboardEvent) {
    if (event.key === 'Enter' || event.key === ' ') event.stopPropagation()
  }

  function selectRow(route: RouteSummary) {
    onSelect(route)
  }

  function handleRowKeyDown(event: KeyboardEvent, route: RouteSummary) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      selectRow(route)
    }
  }

  async function confirmDelete() {
    if (!deletingRoute) return
    setIsDeleting(true)
    setDeleteError(null)
    try {
      await onDelete(deletingRoute)
      setDeletingRoute(null)
    } catch (err) {
      setDeleteError(err instanceof ApiError ? err.message : 'Could not delete this route. Try again.')
    } finally {
      setIsDeleting(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Your routes</CardTitle>
      </CardHeader>
      <CardContent>
        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && (
          <p className="text-sm text-danger">Could not load your routes. Check your connection and try again.</p>
        )}

        {status === 'ready' && routes.length === 0 && (
          <EmptyState
            icon={RouteIcon}
            title="No routes yet"
            description="Build your first route above — pick a departure and arrival airport to get started."
          />
        )}

        {status === 'ready' && routes.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Departure</TableHead>
                <TableHead>Arrival</TableHead>
                <TableHead>Distance</TableHead>
                <TableHead>Block time</TableHead>
                <TableHead>Fare</TableHead>
                <TableHead className="w-10">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {routes.map((route) => {
                const minutes = blockMinutes[route.id]
                return (
                  <TableRow
                    key={route.id}
                    tabIndex={0}
                    role="button"
                    aria-pressed={route.id === selectedId}
                    data-state={route.id === selectedId ? 'selected' : undefined}
                    className={cn(
                      'cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset',
                      route.id === hoveredId && route.id !== selectedId && 'bg-accent/10',
                    )}
                    onClick={() => selectRow(route)}
                    onKeyDown={(event) => handleRowKeyDown(event, route)}
                    onMouseEnter={() => onHover?.(route.id)}
                    onMouseLeave={() => onHover?.(null)}
                  >
                    <TableCell>
                      <span className="font-mono">{route.departureIcao}</span>
                      {route.departureName && <span className="ml-1.5 text-muted-foreground">{route.departureName}</span>}
                    </TableCell>
                    <TableCell>
                      <span className="font-mono">{route.arrivalIcao}</span>
                      {route.arrivalName && <span className="ml-1.5 text-muted-foreground">{route.arrivalName}</span>}
                    </TableCell>
                    <TableCell className="tabular-nums">{fmt.distance(route.distanceNm)}</TableCell>
                    <TableCell className="tabular-nums">
                      {minutes !== undefined ? fmt.duration(minutes) : <Skeleton className="h-4 w-10" />}
                    </TableCell>
                    <TableCell className="tabular-nums">{fmt.money(route.baseFare)}</TableCell>
                    <TableCell>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        className="size-8 text-muted-foreground hover:text-danger"
                        aria-label={`Delete route ${route.departureIcao} to ${route.arrivalIcao}`}
                        onClick={(event) => requestDelete(event, route)}
                        onKeyDown={stopRowActivation}
                      >
                        <Trash2 className="size-4" />
                      </Button>
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>

      <Dialog open={deletingRoute !== null} onOpenChange={(open) => !open && setDeletingRoute(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete this route?</DialogTitle>
            <DialogDescription>
              {deletingRoute && `${deletingRoute.departureIcao} → ${deletingRoute.arrivalIcao} will be removed. This can't be undone.`}
            </DialogDescription>
          </DialogHeader>
          {deleteError && <p className="text-sm text-danger">{deleteError}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setDeletingRoute(null)} disabled={isDeleting}>
              Cancel
            </Button>
            <Button type="button" variant="destructive" onClick={confirmDelete} disabled={isDeleting}>
              {isDeleting ? 'Deleting…' : 'Delete'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
