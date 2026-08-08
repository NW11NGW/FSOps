import { useMemo, useState } from 'react'
import { AlertCircle, PlaneTakeoff, Search, Sparkles } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'

import type { RouteRow } from './routeRow'

interface RouteSelectorProps {
  rows: RouteRow[]
  selectedRouteId: string | null
  onSelect: (row: RouteRow) => void
  /** GET /flights/options isn't available - aircraft availability genuinely can't be determined. */
  optionsUnavailable: boolean
}

function matchesQuery(row: RouteRow, query: string): boolean {
  const haystack = [row.departureIcao, row.arrivalIcao, row.departureName, row.arrivalName, row.flightNumber]
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
  return haystack.includes(query.toLowerCase())
}

function RouteRowCard({
  row,
  selected,
  emphasised,
  onSelect,
}: {
  row: RouteRow
  selected: boolean
  emphasised: boolean
  onSelect: () => void
}) {
  const { fmt } = useSettings()

  return (
    <button
      type="button"
      onClick={onSelect}
      className={cn(
        'w-full rounded-md border p-3 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        selected
          ? 'border-accent bg-accent/10'
          : emphasised
            ? 'border-accent/50 bg-accent/5 hover:border-accent'
            : row.isFlyable
              ? 'border-border hover:border-accent/50 hover:bg-muted/40'
              : 'border-border bg-muted/20 opacity-80 hover:opacity-100',
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-sm font-semibold tracking-tight">
              {row.departureIcao} <span className="text-muted-foreground">→</span> {row.arrivalIcao}
            </span>
            {row.flightNumber && (
              <Badge variant="outline" className="font-mono">
                {row.flightNumber}
              </Badge>
            )}
            {emphasised && (
              <Badge variant="success" className="gap-1">
                <Sparkles className="size-3" />
                Ready now
              </Badge>
            )}
            {!row.isFlyable && (
              <Badge variant="muted">Not flyable</Badge>
            )}
          </div>
          {(row.departureName || row.arrivalName) && (
            <p className="mt-0.5 truncate text-xs text-muted-foreground">
              {[row.departureName, row.arrivalName].filter(Boolean).join(' → ')}
            </p>
          )}
        </div>
        <div className="shrink-0 text-right text-xs tabular-nums text-muted-foreground">
          <p>{fmt.distance(row.distanceNm)}</p>
          {row.blockMinutes !== null && <p>{fmt.duration(row.blockMinutes)}</p>}
        </div>
      </div>

      {!row.isFlyable && row.reason && (
        <p className="mt-2 flex items-start gap-1.5 text-xs text-warning">
          <AlertCircle className="mt-0.5 size-3.5 shrink-0" />
          {row.reason}
        </p>
      )}

      {row.isFlyable && row.availableAircraft.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {row.availableAircraft.map((aircraft) => (
            <Badge key={aircraft.fleetAircraftId} variant="secondary" className="font-mono">
              {aircraft.registration || aircraft.icaoType}
            </Badge>
          ))}
        </div>
      )}
    </button>
  )
}

/**
 * Route picker for the pre-flight Fly screen. Splits "ready now" routes (aircraft physically at
 * the departure airport) out to the top so a return leg is genuinely easy to spot, then lists
 * flyable and not-flyable routes with the reason for each. Falls back to a flat, aircraft-unknown
 * list when GET /flights/options isn't deployed yet.
 */
export function RouteSelector({ rows, selectedRouteId, onSelect, optionsUnavailable }: RouteSelectorProps) {
  const [query, setQuery] = useState('')

  const filtered = useMemo(() => (query.trim() ? rows.filter((row) => matchesQuery(row, query)) : rows), [rows, query])

  const readyNow = optionsUnavailable ? [] : filtered.filter((row) => row.isFlyable && row.availableAircraft.length > 0)
  const readyNowIds = new Set(readyNow.map((row) => row.routeId))
  const rest = filtered.filter((row) => !readyNowIds.has(row.routeId))
  const flyable = rest.filter((row) => row.isFlyable)
  const notFlyable = rest.filter((row) => !row.isFlyable)

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Choose a route</CardTitle>
        {optionsUnavailable && (
          <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <AlertCircle className="size-3.5" />
            Aircraft availability isn&rsquo;t available yet — showing all your routes.
          </p>
        )}
      </CardHeader>
      <CardContent className="space-y-4">
        {rows.length > 5 && (
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Search routes…"
              aria-label="Search routes"
              className="pl-9"
            />
          </div>
        )}

        {rows.length === 0 && (
          <div className="flex flex-col items-center gap-2 py-10 text-center">
            <PlaneTakeoff className="size-8 text-muted-foreground" />
            <p className="text-sm font-medium">No routes yet</p>
            <p className="text-sm text-muted-foreground">Build a route on the Routes page before you can fly.</p>
          </div>
        )}

        {readyNow.length > 0 && (
          <div className="space-y-2">
            <p className="text-xs font-medium uppercase tracking-wide text-success">Ready now</p>
            <div className="space-y-2">
              {readyNow.map((row) => (
                <RouteRowCard key={row.routeId} row={row} selected={row.routeId === selectedRouteId} emphasised onSelect={() => onSelect(row)} />
              ))}
            </div>
          </div>
        )}

        {flyable.length > 0 && (
          <div className="space-y-2">
            {readyNow.length > 0 && <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Other routes</p>}
            <div className="space-y-2">
              {flyable.map((row) => (
                <RouteRowCard key={row.routeId} row={row} selected={row.routeId === selectedRouteId} emphasised={false} onSelect={() => onSelect(row)} />
              ))}
            </div>
          </div>
        )}

        {notFlyable.length > 0 && (
          <div className="space-y-2">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Not flyable right now</p>
            <div className="space-y-2">
              {notFlyable.map((row) => (
                <RouteRowCard key={row.routeId} row={row} selected={row.routeId === selectedRouteId} emphasised={false} onSelect={() => onSelect(row)} />
              ))}
            </div>
          </div>
        )}

        {rows.length > 0 && filtered.length === 0 && (
          <p className="py-6 text-center text-sm text-muted-foreground">No routes match &ldquo;{query}&rdquo;.</p>
        )}
      </CardContent>
    </Card>
  )
}
