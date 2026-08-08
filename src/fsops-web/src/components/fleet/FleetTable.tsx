import type { ReactNode } from 'react'
import { AlertTriangle, Plane } from 'lucide-react'

import { ConditionBar } from '@/components/fleet/ConditionBar'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { EmptyState } from '@/components/shared/EmptyState'
import { useSettings } from '@/hooks/useSettings'
import type { FleetStatusState } from '@/hooks/useFleet'
import type { FleetAircraftSummary } from '@/types/fleet'

interface FleetTableProps {
  fleet: FleetAircraftSummary[]
  status: FleetStatusState
  emptyAction?: ReactNode
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

export function FleetTable({ fleet, status, emptyAction }: FleetTableProps) {
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
                    <p className="min-w-0 break-words font-medium">{aircraft.registration}</p>
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
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  )
}
