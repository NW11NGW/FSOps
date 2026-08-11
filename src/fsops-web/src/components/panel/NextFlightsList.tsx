import { ArrowRight, PlaneTakeoff } from 'lucide-react'

import { formatCallsign } from '@/lib/callsign'
import type { FlightOption } from '@/types/flight'

interface NextFlightsListProps {
  flights: FlightOption[]
  airlineIcaoCode: string | null
}

/** Compact "what's ready to fly right now" strip - the same flyable-routes list the Fly screen
 *  offers (see useFlightOptions), capped and shrunk for a glance rather than a picker. Read-only:
 *  nothing here is tappable, this is a toolbar readout, not a second way to start a flight. */
export function NextFlightsList({ flights, airlineIcaoCode }: NextFlightsListProps) {
  if (flights.length === 0) {
    return <p className="text-xs text-muted-foreground">No route is ready to fly right now.</p>
  }

  return (
    <ul className="space-y-1.5">
      {flights.map((option) => {
        const callsign = formatCallsign(airlineIcaoCode, option.flightNumber)
        return (
          <li key={option.routeId} className="flex items-center gap-2 text-xs">
            <PlaneTakeoff className="size-3.5 shrink-0 text-muted-foreground" />
            {callsign && <span className="shrink-0 font-mono font-medium text-foreground">{callsign}</span>}
            <span className="flex min-w-0 items-center gap-1 truncate font-mono text-muted-foreground">
              {option.departureIcao}
              <ArrowRight className="size-3 shrink-0" />
              {option.arrivalIcao}
            </span>
          </li>
        )
      })}
    </ul>
  )
}
