import { History } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useSettings } from '@/hooks/useSettings'
import type { FlightHistoryStatus } from '@/hooks/useFlightHistory'
import { formatCallsign } from '@/lib/callsign'
import { assessLanding } from '@/lib/flightFormat'
import type { Flight, FlightStatus } from '@/types/flight'

interface RouteLookupEntry {
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
}

interface FlightHistoryListProps {
  flights: Flight[]
  status: FlightHistoryStatus
  routesById: Record<string, RouteLookupEntry>
  airlineIcaoCode: string | null
  onView: (flight: Flight) => void
}

/* Record<FlightStatus, ...> on purpose rather than a partial map: a status with no entry here
 * renders an undefined variant, which is how the three virtual-pilot outcomes below went unstyled
 * in flight history until 2026-08-14 - GET /flights returns them like any other row. Keeping this
 * total means the next status added to the backend enum fails the build here instead of quietly
 * appearing as an unstyled badge. */
const STATUS_BADGE: Record<FlightStatus, 'success' | 'muted' | 'warning' | 'danger' | 'secondary'> = {
  Completed: 'success',
  InProgress: 'secondary',
  Interrupted: 'warning',
  Abandoned: 'danger',
  // A sector that could not fly. Skipped costs nothing (Casual), Cancelled costs a fee
  // (True-life), and Suspended is the schedule pausing itself around a maintenance check - which is
  // the app working correctly, so it stays neutral rather than reading as a problem.
  Skipped: 'warning',
  Cancelled: 'danger',
  Suspended: 'muted',
}

const DATE_FORMATTER = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium' })

export function FlightHistoryList({ flights, status, routesById, airlineIcaoCode, onView }: FlightHistoryListProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Flight history</CardTitle>
      </CardHeader>
      <CardContent>
        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">Could not load flight history. Check your connection and try again.</p>}

        {status === 'ready' && flights.length === 0 && (
          <EmptyState icon={History} title="No flights yet" description="Flights you complete will show up here with their landing quality and report card." />
        )}

        {status === 'ready' && flights.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Date</TableHead>
                <TableHead>Route</TableHead>
                <TableHead>Aircraft</TableHead>
                <TableHead>Block time</TableHead>
                <TableHead>Landing</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {flights.map((flight) => {
                const route = routesById[flight.routeId]
                const callsign = route ? formatCallsign(airlineIcaoCode, route.flightNumber) : null
                const landing = flight.landingFpmFirst !== null ? assessLanding(flight.landingFpmFirst) : null
                const blockMinutes = flight.outUtc && flight.inUtc ? (Date.parse(flight.inUtc) - Date.parse(flight.outUtc)) / 60000 : null

                return (
                  <TableRow
                    key={flight.id}
                    tabIndex={0}
                    role="button"
                    className="cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
                    onClick={() => onView(flight)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        onView(flight)
                      }
                    }}
                  >
                    <TableCell className="whitespace-nowrap text-xs text-muted-foreground">{DATE_FORMATTER.format(new Date(flight.createdUtc))}</TableCell>
                    <TableCell className="font-mono">
                      {route ? `${route.departureIcao} → ${route.arrivalIcao}` : '—'}
                      {callsign && <span className="ml-2 font-sans text-xs text-muted-foreground">{callsign}</span>}
                    </TableCell>
                    <TableCell className="max-w-[200px] truncate text-xs text-muted-foreground" title={flight.titleFlown}>
                      {flight.titleFlown || '—'}
                    </TableCell>
                    <TableCell className="tabular-nums">{blockMinutes !== null ? fmt.duration(blockMinutes) : '—'}</TableCell>
                    <TableCell className="tabular-nums">
                      {landing ? (
                        <span
                          className={
                            landing.verdict === 'smooth' ? 'text-success' : landing.verdict === 'firm' ? 'text-warning' : 'text-danger'
                          }
                        >
                          {Math.round(landing.displayFpm)} fpm
                        </span>
                      ) : (
                        '—'
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-wrap gap-1">
                        <Badge variant={STATUS_BADGE[flight.status]}>{flight.status}</Badge>
                        {flight.typeMismatch === true && <Badge variant="outline">Type noted</Badge>}
                      </div>
                    </TableCell>
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
