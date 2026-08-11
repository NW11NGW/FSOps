import { Plane } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ExportCsvButton } from '@/components/stats/ExportCsvButton'
import type { CsvColumn } from '@/lib/csv'
import { cn } from '@/lib/utils'
import type { StatsFleetAircraft } from '@/types/stats'

interface FleetUtilisationTableProps {
  status: 'loading' | 'ready' | 'error'
  aircraft: StatsFleetAircraft[]
  periodDays: number
}

/** The sooner of the two check clocks, with which one it is - "how close each airframe is to its
 *  next check" as a single honest figure rather than two competing ones. */
function nextCheck(row: StatsFleetAircraft): { hours: number; type: 'A' | 'C' } {
  return row.hoursToNextACheck <= row.hoursToNextCCheck
    ? { hours: row.hoursToNextACheck, type: 'A' }
    : { hours: row.hoursToNextCCheck, type: 'C' }
}

const CSV_COLUMNS: CsvColumn<StatsFleetAircraft>[] = [
  { key: 'registration', header: 'Registration' },
  { key: 'aircraftTypeName', header: 'Aircraft type' },
  { key: 'status', header: 'Status' },
  { key: 'sectorsFlown', header: 'Sectors flown' },
  { key: 'hoursFlownInPeriod', header: 'Hours flown' },
  { key: 'idleHoursInPeriod', header: 'Idle hours' },
  { key: 'utilisationPercent', header: 'Utilisation %' },
  { key: 'hoursToNextACheck', header: 'Hours to next A-check' },
  { key: 'hoursToNextCCheck', header: 'Hours to next C-check' },
  { key: 'conditionPercent', header: 'Condition %' },
]

export function FleetUtilisationTable({ status, aircraft, periodDays }: FleetUtilisationTableProps) {
  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-4 space-y-0 pb-3">
        <div>
          <CardTitle className="flex items-center gap-2 text-base">
            <Plane className="size-4 text-muted-foreground" />
            Fleet utilisation
          </CardTitle>
          <p className="text-xs text-muted-foreground">Last {periodDays} days. Hours flown are measured from completed flights, not the airframe's lifetime total.</p>
        </div>
        <ExportCsvButton filename={`fsops-fleet-utilisation-last-${periodDays}-days.csv`} rows={aircraft} columns={CSV_COLUMNS} />
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load fleet utilisation. Check your connection and try again.</p>}

        {status === 'ready' && aircraft.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={Plane} title="No aircraft yet" description="Buy or lease an aircraft to see its utilisation here." />
          </div>
        )}

        {status === 'ready' && aircraft.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Aircraft</TableHead>
                <TableHead>Sectors</TableHead>
                <TableHead>Hours flown</TableHead>
                <TableHead>Idle</TableHead>
                <TableHead>Utilisation</TableHead>
                <TableHead>Next check</TableHead>
                <TableHead>Condition</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {aircraft.map((row) => {
                const next = nextCheck(row)
                return (
                  <TableRow key={row.fleetAircraftId}>
                    <TableCell>
                      <p className="font-medium">{row.registration}</p>
                      <p className="text-xs text-muted-foreground">{row.aircraftTypeName}</p>
                    </TableCell>
                    <TableCell className="tabular-nums">{row.sectorsFlown}</TableCell>
                    <TableCell className="tabular-nums">{row.hoursFlownInPeriod.toFixed(1)}h</TableCell>
                    <TableCell className="tabular-nums text-muted-foreground">{row.idleHoursInPeriod.toFixed(1)}h</TableCell>
                    <TableCell className="w-32">
                      <div className="flex items-center gap-2">
                        <div className="h-1.5 w-16 overflow-hidden rounded-full bg-muted">
                          <div className="h-full rounded-full bg-accent" style={{ width: `${Math.min(100, row.utilisationPercent)}%` }} />
                        </div>
                        <span className="text-xs tabular-nums text-muted-foreground">{row.utilisationPercent.toFixed(0)}%</span>
                      </div>
                    </TableCell>
                    <TableCell className={cn('tabular-nums', next.hours < 25 && 'font-medium text-warning')}>
                      {Math.round(next.hours)}h to {next.type}-check
                    </TableCell>
                    <TableCell className="tabular-nums">{row.conditionPercent.toFixed(0)}%</TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}
