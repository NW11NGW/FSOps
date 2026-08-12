import { Link } from 'react-router-dom'
import { Info, RadioTower } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useVatsimHistory } from '@/hooks/useVatsimHistory'

const DATE_FORMATTER = new Intl.DateTimeFormat('en-US', {
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  timeZone: 'UTC',
})

/**
 * G9 - "which of my flights were flown online". Deliberately labelled throughout as FSOps' OWN
 * record, never as VATSIM's - it is built entirely from Flight.vatsimOnline (set by G8's
 * corroboration, not a second call to VATSIM). See VatsimEndpoints.GetHistoryAsync's own doc for
 * why a true VATSIM network history isn't something FSOps can honestly offer without an API key it
 * doesn't have.
 */
export function VatsimHistoryCard() {
  const { status, data } = useVatsimHistory()

  return (
    <Card className="mt-4">
      <CardHeader className="flex-row items-center justify-between space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
          <RadioTower className="size-4" />
          Flown online
        </CardTitle>
      </CardHeader>
      <CardContent>
        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-4 w-2/3" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        )}

        {status === 'error' && (
          <p className="text-sm text-muted-foreground">Could not load your VATSIM history. Check your connection and try again.</p>
        )}

        {status === 'ready' && data && !data.cidConfigured && (
          <div className="flex items-start gap-2 text-sm text-muted-foreground">
            <Info className="mt-0.5 size-4 shrink-0" />
            <span>
              Set your VATSIM CID in{' '}
              <Link to="/settings" className="text-accent underline-offset-2 hover:underline">
                Settings
              </Link>{' '}
              to have FSOps corroborate your flights against the network — entirely optional.
            </span>
          </div>
        )}

        {status === 'ready' && data && data.cidConfigured && data.flights.length === 0 && (
          <p className="text-sm text-muted-foreground">
            No flights have been corroborated against VATSIM yet — fly a tracked sector while connected and it will
            show up here.
          </p>
        )}

        {status === 'ready' && data && data.flights.length > 0 && (
          <>
            <ul className="space-y-2">
              {data.flights.slice(0, 8).map((entry) => (
                <li key={entry.flightId} className="flex flex-wrap items-center justify-between gap-2 text-sm">
                  <div className="flex min-w-0 items-center gap-2">
                    <span className="font-mono text-foreground">
                      {entry.departureIcao ?? '????'} → {entry.arrivalIcao ?? '????'}
                    </span>
                    <span className="text-xs text-muted-foreground">{DATE_FORMATTER.format(new Date(entry.completedUtc))}</span>
                  </div>
                  <Badge variant={entry.online ? 'success' : 'muted'}>
                    {entry.online ? `Online${entry.callsign ? ` as ${entry.callsign}` : ''}` : 'Not corroborated'}
                  </Badge>
                </li>
              ))}
            </ul>
            <p className="mt-3 text-xs text-muted-foreground">
              FSOps' own record, built from telemetry corroborated against the public VATSIM feed while each flight
              was tracked — not a pull from VATSIM's own history.
            </p>
          </>
        )}
      </CardContent>
    </Card>
  )
}
