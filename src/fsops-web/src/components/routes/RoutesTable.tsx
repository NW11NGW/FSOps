import { Fragment, useMemo, useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'
import { ArrowLeftRight, ChevronDown, ChevronRight, Route as RouteIcon, Trash2, TriangleAlert } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
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

interface RoutePair {
  key: string
  outbound: RouteSummary
  /** Null only if the reverse leg genuinely doesn't exist yet - the server self-heals this on
   *  the next load, so it should only ever be visible for a moment. */
  inbound: RouteSummary | null
}

/** Pairs up outbound/return legs by returnRouteId. `routes` arrives sorted by creation time, so
 *  the leg created first (the one POST /routes was called with) is treated as the outbound. */
function groupRoutePairs(routes: RouteSummary[]): RoutePair[] {
  const byId = new Map(routes.map((route) => [route.id, route]))
  const seen = new Set<string>()
  const pairs: RoutePair[] = []

  for (const route of routes) {
    if (seen.has(route.id)) continue
    seen.add(route.id)

    const partner = route.returnRouteId ? byId.get(route.returnRouteId) : undefined
    if (partner && !seen.has(partner.id)) {
      seen.add(partner.id)
      pairs.push({ key: route.id, outbound: route, inbound: partner })
    } else {
      pairs.push({ key: route.id, outbound: route, inbound: null })
    }
  }

  return pairs
}

/** The airline's saved routes (GET /routes), grouped into round-trip pairs. Each pair expands to
 *  show both legs; deleting a pair removes both legs together. */
export function RoutesTable({ routes, status, blockMinutes, selectedId, hoveredId, onSelect, onDelete, onHover }: RoutesTableProps) {
  const { fmt } = useSettings()
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [deletingPair, setDeletingPair] = useState<RoutePair | null>(null)
  const [isDeleting, setIsDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const pairs = useMemo(() => groupRoutePairs(routes), [routes])

  function toggleExpanded(event: MouseEvent | KeyboardEvent, key: string) {
    event.stopPropagation()
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  function requestDelete(event: MouseEvent, pair: RoutePair) {
    event.stopPropagation()
    setDeleteError(null)
    setDeletingPair(pair)
  }

  function stopRowActivation(event: KeyboardEvent) {
    if (event.key === 'Enter' || event.key === ' ') event.stopPropagation()
  }

  function handleRowKeyDown(event: KeyboardEvent, route: RouteSummary) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onSelect(route)
    }
  }

  async function confirmDelete() {
    if (!deletingPair) return
    setIsDeleting(true)
    setDeleteError(null)
    try {
      await onDelete(deletingPair.outbound)
      setDeletingPair(null)
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

        {status === 'ready' && pairs.length === 0 && (
          <EmptyState
            icon={RouteIcon}
            title="No routes yet"
            description="Build your first route above — pick a departure and arrival airport to get started. Every route you create automatically includes its return leg."
          />
        )}

        {status === 'ready' && pairs.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-8">
                  <span className="sr-only">Expand</span>
                </TableHead>
                <TableHead>Route</TableHead>
                <TableHead>Flight numbers</TableHead>
                <TableHead>Distance</TableHead>
                <TableHead>Block time</TableHead>
                <TableHead>Fare</TableHead>
                <TableHead className="w-10">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {pairs.map((pair) => {
                const { outbound, inbound } = pair
                const isExpanded = expanded.has(pair.key)
                const isPairSelected = selectedId === outbound.id || (inbound && selectedId === inbound.id)
                const isPairHovered = hoveredId === outbound.id || (inbound && hoveredId === inbound.id)
                const outboundMinutes = blockMinutes[outbound.id]
                const inboundMinutes = inbound ? blockMinutes[inbound.id] : undefined
                const faresMatch = inbound ? outbound.baseFare === inbound.baseFare : true

                return (
                  <Fragment key={pair.key}>
                    <TableRow
                      tabIndex={0}
                      role="button"
                      aria-pressed={Boolean(isPairSelected)}
                      aria-expanded={isExpanded}
                      data-state={isPairSelected ? 'selected' : undefined}
                      className={cn(
                        'cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset',
                        isPairHovered && !isPairSelected && 'bg-accent/10',
                      )}
                      onClick={() => onSelect(outbound)}
                      onKeyDown={(event) => handleRowKeyDown(event, outbound)}
                      onMouseEnter={() => onHover?.(outbound.id)}
                      onMouseLeave={() => onHover?.(null)}
                    >
                      <TableCell>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-7 text-muted-foreground"
                          aria-label={isExpanded ? 'Collapse leg details' : 'Expand leg details'}
                          onClick={(event) => toggleExpanded(event, pair.key)}
                          onKeyDown={stopRowActivation}
                        >
                          {isExpanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                        </Button>
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1.5">
                          <span className="font-mono font-medium">{outbound.departureIcao}</span>
                          <ArrowLeftRight className="size-3.5 text-muted-foreground" aria-hidden="true" />
                          <span className="font-mono font-medium">{outbound.arrivalIcao}</span>
                          {!inbound && (
                            <span
                              className="ml-1 inline-flex items-center gap-1 text-warning"
                              title="This route's return leg hasn't been created yet - reload the page to have it repaired automatically."
                            >
                              <TriangleAlert className="size-3.5" />
                            </span>
                          )}
                        </div>
                        {outbound.departureName && outbound.arrivalName && (
                          <p className="mt-0.5 truncate text-xs text-muted-foreground">
                            {outbound.departureName} ⇄ {outbound.arrivalName}
                          </p>
                        )}
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {outbound.flightNumber && <Badge variant="outline">{outbound.flightNumber}</Badge>}
                          {inbound?.flightNumber && <Badge variant="outline">{inbound.flightNumber}</Badge>}
                          {!outbound.flightNumber && !inbound?.flightNumber && <span className="text-muted-foreground">—</span>}
                        </div>
                      </TableCell>
                      <TableCell className="tabular-nums">{fmt.distance(outbound.distanceNm)}</TableCell>
                      <TableCell className="tabular-nums">
                        {outboundMinutes !== undefined ? fmt.duration(outboundMinutes) : <Skeleton className="h-4 w-10" />}
                      </TableCell>
                      <TableCell className="tabular-nums">
                        {faresMatch ? fmt.money(outbound.baseFare) : `${fmt.money(outbound.baseFare)} / ${fmt.money(inbound!.baseFare)}`}
                      </TableCell>
                      <TableCell>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-8 text-muted-foreground hover:text-danger"
                          aria-label={`Delete route ${outbound.departureIcao} to ${outbound.arrivalIcao} and its return leg`}
                          onClick={(event) => requestDelete(event, pair)}
                          onKeyDown={stopRowActivation}
                        >
                          <Trash2 className="size-4" />
                        </Button>
                      </TableCell>
                    </TableRow>

                    {isExpanded && (
                      <TableRow className="bg-muted/20 hover:bg-muted/20">
                        <TableCell />
                        <TableCell colSpan={6} className="py-2">
                          <dl className="grid gap-2 sm:grid-cols-2">
                            <button
                              type="button"
                              onClick={() => onSelect(outbound)}
                              className="flex items-center justify-between gap-3 rounded-md border border-border p-2 text-left text-xs hover:border-ring"
                            >
                              <span className="flex items-center gap-1.5">
                                <Badge variant="secondary">Outbound</Badge>
                                <span className="font-mono">{outbound.departureIcao} → {outbound.arrivalIcao}</span>
                                {outbound.flightNumber && <span className="text-muted-foreground">FL{outbound.flightNumber}</span>}
                              </span>
                              <span className="tabular-nums text-muted-foreground">
                                {fmt.distance(outbound.distanceNm)} ·{' '}
                                {outboundMinutes !== undefined ? fmt.duration(outboundMinutes) : '—'} · {fmt.money(outbound.baseFare)}
                              </span>
                            </button>
                            {inbound ? (
                              <button
                                type="button"
                                onClick={() => onSelect(inbound)}
                                className="flex items-center justify-between gap-3 rounded-md border border-border p-2 text-left text-xs hover:border-ring"
                              >
                                <span className="flex items-center gap-1.5">
                                  <Badge variant="muted">Return</Badge>
                                  <span className="font-mono">{inbound.departureIcao} → {inbound.arrivalIcao}</span>
                                  {inbound.flightNumber && <span className="text-muted-foreground">FL{inbound.flightNumber}</span>}
                                </span>
                                <span className="tabular-nums text-muted-foreground">
                                  {fmt.distance(inbound.distanceNm)} ·{' '}
                                  {inboundMinutes !== undefined ? fmt.duration(inboundMinutes) : '—'} · {fmt.money(inbound.baseFare)}
                                </span>
                              </button>
                            ) : (
                              <p className="flex items-center gap-1.5 rounded-md border border-dashed border-warning/40 p-2 text-xs text-warning">
                                <TriangleAlert className="size-3.5 shrink-0" />
                                Return leg not created yet — reload the page to repair it.
                              </p>
                            )}
                          </dl>
                        </TableCell>
                      </TableRow>
                    )}
                  </Fragment>
                )
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>

      <Dialog open={deletingPair !== null} onOpenChange={(open) => !open && setDeletingPair(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete this route?</DialogTitle>
            <DialogDescription>
              {deletingPair &&
                (deletingPair.inbound
                  ? `Both legs will be removed: ${deletingPair.outbound.departureIcao} → ${deletingPair.outbound.arrivalIcao} and ${deletingPair.inbound.departureIcao} → ${deletingPair.inbound.arrivalIcao}. This can't be undone.`
                  : `${deletingPair.outbound.departureIcao} → ${deletingPair.outbound.arrivalIcao} will be removed. This can't be undone.`)}
            </DialogDescription>
          </DialogHeader>
          {deleteError && <p className="text-sm text-danger">{deleteError}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setDeletingPair(null)} disabled={isDeleting}>
              Cancel
            </Button>
            <Button type="button" variant="destructive" onClick={confirmDelete} disabled={isDeleting}>
              {isDeleting ? 'Deleting…' : 'Delete both legs'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
