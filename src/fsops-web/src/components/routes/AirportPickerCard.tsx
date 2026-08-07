import { ArrowLeftRight, MapPin, Ruler, X } from 'lucide-react'

import { AirportSearch } from '@/components/shared/AirportSearch'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useSettings } from '@/hooks/useSettings'
import type { AirportSummary } from '@/types/airport'

interface AirportPickerCardProps {
  departure: AirportSummary | null
  arrival: AirportSummary | null
  onSelectDeparture: (airport: AirportSummary) => void
  onSelectArrival: (airport: AirportSummary) => void
  onClearDeparture: () => void
  onClearArrival: () => void
  onSwap: () => void
}

function SelectedAirport({ airport, onClear }: { airport: AirportSummary; onClear: () => void }) {
  const { fmt } = useSettings()

  return (
    <div className="space-y-2 rounded-md border border-border bg-muted/40 p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-mono text-sm font-semibold">{airport.icao}</span>
            {airport.iata && <Badge variant="outline">{airport.iata}</Badge>}
          </div>
          <p className="mt-0.5 truncate text-sm text-muted-foreground">{airport.name}</p>
        </div>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="size-7 shrink-0"
          onClick={onClear}
          aria-label={`Change ${airport.icao} selection`}
        >
          <X className="size-3.5" />
        </Button>
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
        <span className="flex items-center gap-1">
          <MapPin className="size-3" />
          {[airport.municipality, airport.country].filter(Boolean).join(', ') || '—'}
        </span>
        <span className="flex items-center gap-1">
          <Ruler className="size-3" />
          {airport.longestRunwayFt !== null ? `${fmt.altitude(airport.longestRunwayFt)} longest runway` : 'No runway data'}
        </span>
      </div>
    </div>
  )
}

/** From/To airport pickers (reusing the shared AirportSearch combobox) plus a swap button. */
export function AirportPickerCard({
  departure,
  arrival,
  onSelectDeparture,
  onSelectArrival,
  onClearDeparture,
  onClearArrival,
  onSwap,
}: AirportPickerCardProps) {
  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between gap-4 space-y-0">
        <div>
          <CardTitle className="text-base">Build a route</CardTitle>
          <p className="mt-1 text-sm text-muted-foreground">Pick a departure and arrival airport to plan the sector.</p>
        </div>
        <Button
          type="button"
          variant="outline"
          size="icon"
          onClick={onSwap}
          aria-label="Swap departure and arrival"
          disabled={!departure && !arrival}
        >
          <ArrowLeftRight className="size-4" />
        </Button>
      </CardHeader>
      <CardContent className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-1.5">
          <span id="route-from-label" className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            From
          </span>
          {departure ? (
            <SelectedAirport airport={departure} onClear={onClearDeparture} />
          ) : (
            <AirportSearch onSelect={onSelectDeparture} placeholder="Search departure airport…" />
          )}
        </div>
        <div className="space-y-1.5">
          <span id="route-to-label" className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
            To
          </span>
          {arrival ? (
            <SelectedAirport airport={arrival} onClear={onClearArrival} />
          ) : (
            <AirportSearch onSelect={onSelectArrival} placeholder="Search arrival airport…" />
          )}
        </div>
      </CardContent>
    </Card>
  )
}
