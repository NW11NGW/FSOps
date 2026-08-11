import { Users } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/shared/EmptyState'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ExportCsvButton } from '@/components/stats/ExportCsvButton'
import type { CsvColumn } from '@/lib/csv'
import type { StatsPilotLogbookEntry } from '@/types/stats'

interface PilotLogbookTableProps {
  status: 'loading' | 'ready' | 'error'
  pilots: StatsPilotLogbookEntry[]
  periodDays: number
}

const CSV_COLUMNS: CsvColumn<StatsPilotLogbookEntry>[] = [
  { key: 'name', header: 'Pilot' },
  { key: 'isPlayer', header: 'Player' },
  { key: 'sectorsFlown', header: 'Sectors flown' },
  { key: 'hoursFlown', header: 'Hours flown' },
  { key: 'onTimePercent', header: 'On time %' },
  { key: 'averageLandingFpm', header: 'Average landing fpm' },
]

export function PilotLogbookTable({ status, pilots, periodDays }: PilotLogbookTableProps) {
  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-4 space-y-0 pb-3">
        <div>
          <CardTitle className="flex items-center gap-2 text-base">
            <Users className="size-4 text-muted-foreground" />
            Pilot logbook
          </CardTitle>
          <p className="text-xs text-muted-foreground">Last {periodDays} days, sectors and hours from completed flights - including your own.</p>
        </div>
        <ExportCsvButton filename={`fsops-pilot-logbook-last-${periodDays}-days.csv`} rows={pilots} columns={CSV_COLUMNS} />
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load the pilot logbook. Check your connection and try again.</p>}

        {status === 'ready' && pilots.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={Users} title="No pilots yet" description="Hire a virtual pilot, or fly a sector yourself, to start a logbook." />
          </div>
        )}

        {status === 'ready' && pilots.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Pilot</TableHead>
                <TableHead>Sectors</TableHead>
                <TableHead>Hours</TableHead>
                <TableHead>On time</TableHead>
                <TableHead>Avg landing</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {pilots.map((pilot) => (
                <TableRow key={pilot.pilotId}>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <p className="font-medium">{pilot.name}</p>
                      {pilot.isPlayer && (
                        <Badge variant="secondary" className="text-[10px]">
                          You
                        </Badge>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="tabular-nums">{pilot.sectorsFlown}</TableCell>
                  <TableCell className="tabular-nums">{pilot.hoursFlown.toFixed(1)}h</TableCell>
                  <TableCell className="tabular-nums">
                    {pilot.onTimePercent === null ? <span className="text-muted-foreground">Not measured</span> : `${pilot.onTimePercent.toFixed(0)}%`}
                  </TableCell>
                  <TableCell className="tabular-nums">
                    {pilot.averageLandingFpm === null ? <span className="text-muted-foreground">Not measured</span> : `${Math.round(pilot.averageLandingFpm)} fpm`}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}
