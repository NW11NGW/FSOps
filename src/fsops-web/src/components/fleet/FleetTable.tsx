import type { ReactNode } from 'react'
import { AlertTriangle, Banknote, LogOut, Pencil, Plane, ShieldCheck, Users, Wrench } from 'lucide-react'

import { ConditionBar } from '@/components/fleet/ConditionBar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { EmptyState } from '@/components/shared/EmptyState'
import { useSettings } from '@/hooks/useSettings'
import type { FleetStatusState } from '@/hooks/useFleet'
import type { FleetAircraftSummary } from '@/types/fleet'

interface FleetTableProps {
  fleet: FleetAircraftSummary[]
  status: FleetStatusState
  emptyAction?: ReactNode
  onRename?: (aircraft: FleetAircraftSummary) => void
  /** Toggle PUT /fleet/{id}/reservation - the player can always release the aircraft held back for
   *  them, including their first. Omitted entirely hides the reservation column, same optional-prop convention as onRename. */
  onToggleReservation?: (aircraft: FleetAircraftSummary) => void
  /** Opens the sell/end-lease dialog appropriate to this aircraft's ownership - see
   *  FleetDisposalEndpoints. Omitted entirely hides the action column. */
  onDispose?: (aircraft: FleetAircraftSummary) => void
  /** Opens PerformMaintenanceDialog for this aircraft, so a check can be brought forward at a
   *  moment of the player's choosing. Omitted entirely hides the button; shares the actions column
   *  with onDispose rather than adding a second column. */
  onPerformMaintenance?: (aircraft: FleetAircraftSummary) => void
}

function statusBadge(aircraft: FleetAircraftSummary) {
  switch (aircraft.status) {
    case 'InMaintenance':
      return (
        <Badge variant="danger" className="gap-1">
          <AlertTriangle className="size-3" />
          Grounded
        </Badge>
      )
    case 'InFlight':
      return <Badge variant="warning">In flight</Badge>
    default:
      return <Badge variant="success">Active</Badge>
  }
}

function formatUntil(iso: string | null): string | null {
  if (!iso) return null
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return null
  return date.toLocaleString(undefined, { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
}

export function FleetTable({ fleet, status, emptyAction, onRename, onToggleReservation, onDispose, onPerformMaintenance }: FleetTableProps) {
  const { fmt } = useSettings()

  if (status === 'loading') {
    return (
      <div className="space-y-2">
        {[0, 1, 2].map((i) => (
          <Skeleton key={i} className="h-14 w-full" />
        ))}
      </div>
    )
  }

  if (status === 'error') {
    return (
      <EmptyState
        icon={AlertTriangle}
        title="Could not load your fleet"
        description="Check your connection and try again."
      />
    )
  }

  if (fleet.length === 0) {
    return (
      <EmptyState
        icon={Plane}
        title="No aircraft yet"
        description="Lease or buy your first additional aircraft to start growing your fleet."
        action={emptyAction}
      />
    )
  }

  return (
    <div className="rounded-lg border border-border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Aircraft</TableHead>
            <TableHead>Status</TableHead>
            <TableHead>Location</TableHead>
            <TableHead>Ownership</TableHead>
            <TableHead>Airframe hrs</TableHead>
            <TableHead>Next A-check</TableHead>
            <TableHead>Next C-check</TableHead>
            <TableHead>Condition</TableHead>
            <TableHead>Fuel on board</TableHead>
            {onToggleReservation && <TableHead>Reservation</TableHead>}
            {(onDispose || onPerformMaintenance) && (
              <TableHead className="text-right">
                <span className="sr-only">Actions</span>
              </TableHead>
            )}
          </TableRow>
        </TableHeader>
        <TableBody>
          {fleet.map((aircraft) => (
            <TableRow key={aircraft.id}>
              <TableCell>
                <div className="flex items-center gap-2">
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent/15 text-accent">
                    <Plane className="size-4" />
                  </div>
                  <div className="min-w-0">
                    <div className="flex min-w-0 items-center gap-1">
                      <p className="min-w-0 break-words font-medium">{aircraft.registration}</p>
                      {onRename && (
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-6 shrink-0"
                          onClick={() => onRename(aircraft)}
                          aria-label={`Rename ${aircraft.registration}`}
                        >
                          <Pencil className="size-3" />
                        </Button>
                      )}
                    </div>
                    <p className="min-w-0 break-words text-xs text-muted-foreground">{aircraft.aircraftTypeName}</p>
                  </div>
                </div>
              </TableCell>
              <TableCell>
                <div className="space-y-1">
                  {statusBadge(aircraft)}
                  {aircraft.groundedReason && (
                    <p className="min-w-0 max-w-[220px] break-words text-xs text-muted-foreground">
                      {aircraft.groundedReason}
                      {aircraft.groundedUntilUtc && formatUntil(aircraft.groundedUntilUtc) && (
                        <> ({formatUntil(aircraft.groundedUntilUtc)})</>
                      )}
                    </p>
                  )}
                </div>
              </TableCell>
              <TableCell className="font-mono text-xs">{aircraft.locationIcao}</TableCell>
              <TableCell>
                <Badge variant={aircraft.ownership === 'Owned' ? 'default' : 'secondary'}>{aircraft.ownership}</Badge>
              </TableCell>
              <TableCell className="tabular-nums">{Math.round(aircraft.airframeHours).toLocaleString()}</TableCell>
              <TableCell className="tabular-nums">{Math.round(aircraft.hoursToNextACheck).toLocaleString()} h</TableCell>
              <TableCell className="tabular-nums">{Math.round(aircraft.hoursToNextCCheck).toLocaleString()} h</TableCell>
              <TableCell>
                <ConditionBar percent={aircraft.conditionPercent} />
              </TableCell>
              <TableCell className="tabular-nums">{fmt.weight(aircraft.fuelOnBoardKg)}</TableCell>
              {onToggleReservation && (
                <TableCell>
                  <div className="flex flex-col items-start gap-1">
                    <Tooltip>
                      <TooltipTrigger asChild>
                        {aircraft.reservedForPlayer ? (
                          <Badge variant="warning" className="gap-1">
                            <ShieldCheck className="size-3" />
                            Reserved for you
                          </Badge>
                        ) : (
                          <Badge variant="muted" className="gap-1">
                            <Users className="size-3" />
                            Available to pilots
                          </Badge>
                        )}
                      </TooltipTrigger>
                      <TooltipContent>
                        {aircraft.reservedForPlayer
                          ? 'Kept free for you to fly - never offered to virtual pilots.'
                          : 'Available to virtual pilots to schedule - not held back for you.'}
                      </TooltipContent>
                    </Tooltip>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      className="h-auto px-0 text-xs font-medium text-muted-foreground hover:text-foreground hover:bg-transparent"
                      onClick={() => onToggleReservation(aircraft)}
                    >
                      {aircraft.reservedForPlayer ? 'Release to pilots' : 'Reserve for you'}
                    </Button>
                  </div>
                </TableCell>
              )}
              {(onDispose || onPerformMaintenance) && (
                <TableCell className="text-right">
                  <div className="flex items-center justify-end gap-1.5">
                    {onPerformMaintenance && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={aircraft.status === 'InFlight'}
                        onClick={() => onPerformMaintenance(aircraft)}
                      >
                        <Wrench className="size-3.5" />
                        Maintenance
                      </Button>
                    )}
                    {onDispose && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={aircraft.status === 'InFlight'}
                        onClick={() => onDispose(aircraft)}
                      >
                        {aircraft.ownership === 'Owned' ? (
                          <>
                            <Banknote className="size-3.5" />
                            Sell
                          </>
                        ) : (
                          <>
                            <LogOut className="size-3.5" />
                            End lease
                          </>
                        )}
                      </Button>
                    )}
                  </div>
                </TableCell>
              )}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  )
}
