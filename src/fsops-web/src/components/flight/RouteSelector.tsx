import { useMemo, useState } from 'react'
import { AlertCircle, PlaneTakeoff, Search, Sparkles } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'

import type { RoutePairRow } from './routeRow'

interface RouteSelectorProps {
  pairs: RoutePairRow[]
  selectedPairId: string | null
  onSelect: (pair: RoutePairRow) => void
  /** GET /flights/options isn't available - aircraft availability genuinely can't be determined. */
  optionsUnavailable: boolean
}

function matchesQuery(pair: RoutePairRow, query: string): boolean {
  const legs = pair.other ? [pair.active, pair.other] : [pair.active]
  const haystack = legs
    .flatMap((leg) => [leg.departureIcao, leg.arrivalIcao, leg.departureName, leg.arrivalName, leg.flightNumber])
    .filter(Boolean)
    .join(' ')
    .toLowerCase()
  return haystack.includes(query.toLowerCase())
}

function RoutePairCard({
  pair,
  selected,
  emphasised,
  onSelect,
}: {
  pair: RoutePairRow
  selected: boolean
  emphasised: boolean
  onSelect: () => void
}) {
  const { fmt } = useSettings()
  const active = pair.active
  const isFlyable = pair.isFlyable
  const aircraftUnknown = active.aircraftUnknown
  const directionLabel = pair.activeIsReturn ? 'Return available' : 'Outbound available'

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
            : isFlyable
              ? 'border-border hover:border-accent/50 hover:bg-muted/40'
              : 'border-border bg-muted/20 opacity-80 hover:opacity-100',
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-sm font-semibold tracking-tight">
              {pair.headerDepartureIcao} <span className="text-muted-foreground">⇄</span> {pair.headerArrivalIcao}
            </span>
            {active.flightNumber && (
              <Badge variant="outline" className="font-mono">
                {active.flightNumber}
              </Badge>
            )}
            {emphasised && (
              <Badge variant="success" className="gap-1">
                <Sparkles className="size-3" />
                Ready now
              </Badge>
            )}
            {isFlyable && !aircraftUnknown && (
              <Badge variant="secondary">{directionLabel}</Badge>
            )}
            {!isFlyable && <Badge variant="muted">Not flyable</Badge>}
          </div>
          {(active.departureName || active.arrivalName) && (
            <p className="mt-0.5 truncate text-xs text-muted-foreground">
              {[active.departureName, active.arrivalName].filter(Boolean).join(' → ')}
            </p>
          )}
        </div>
        <div className="shrink-0 text-right text-xs tabular-nums text-muted-foreground">
          <p>{fmt.distance(active.distanceNm)}</p>
          {active.blockMinutes !== null && <p>{fmt.duration(active.blockMinutes)}</p>}
        </div>
      </div>

      {!isFlyable && pair.reason && (
        <p className="mt-2 flex items-start gap-1.5 text-xs text-warning">
          <AlertCircle className="mt-0.5 size-3.5 shrink-0" />
          {pair.reason}
        </p>
      )}

      {isFlyable && active.availableAircraft.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {active.availableAircraft.map((aircraft) => (
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
 * Route picker for the pre-flight Fly screen. One entry per round-trip pair - whichever direction
 * can actually be flown right now (the aircraft can only be at one end) - rather than one row per
 * leg, so a pair never shows a "not flyable" half that's pure noise. Splits "ready now" pairs
 * (aircraft physically parked at the offered direction's departure) out to the top, then lists
 * other flyable and not-flyable pairs with the reason for each. Falls back to a flat,
 * aircraft-unknown list when GET /flights/options isn't deployed yet.
 */
export function RouteSelector({ pairs, selectedPairId, onSelect, optionsUnavailable }: RouteSelectorProps) {
  const [query, setQuery] = useState('')

  const filtered = useMemo(() => (query.trim() ? pairs.filter((pair) => matchesQuery(pair, query)) : pairs), [pairs, query])

  const readyNow = optionsUnavailable ? [] : filtered.filter((pair) => pair.isFlyable && pair.active.availableAircraft.length > 0)
  const readyNowIds = new Set(readyNow.map((pair) => pair.pairId))
  const rest = filtered.filter((pair) => !readyNowIds.has(pair.pairId))
  const flyable = rest.filter((pair) => pair.isFlyable)
  const notFlyable = rest.filter((pair) => !pair.isFlyable)

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
        {pairs.length > 5 && (
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

        {pairs.length === 0 && (
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
              {readyNow.map((pair) => (
                <RoutePairCard key={pair.pairId} pair={pair} selected={pair.pairId === selectedPairId} emphasised onSelect={() => onSelect(pair)} />
              ))}
            </div>
          </div>
        )}

        {flyable.length > 0 && (
          <div className="space-y-2">
            {readyNow.length > 0 && <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Other routes</p>}
            <div className="space-y-2">
              {flyable.map((pair) => (
                <RoutePairCard key={pair.pairId} pair={pair} selected={pair.pairId === selectedPairId} emphasised={false} onSelect={() => onSelect(pair)} />
              ))}
            </div>
          </div>
        )}

        {notFlyable.length > 0 && (
          <div className="space-y-2">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Not flyable right now</p>
            <div className="space-y-2">
              {notFlyable.map((pair) => (
                <RoutePairCard key={pair.pairId} pair={pair} selected={pair.pairId === selectedPairId} emphasised={false} onSelect={() => onSelect(pair)} />
              ))}
            </div>
          </div>
        )}

        {pairs.length > 0 && filtered.length === 0 && (
          <p className="py-6 text-center text-sm text-muted-foreground">No routes match &ldquo;{query}&rdquo;.</p>
        )}
      </CardContent>
    </Card>
  )
}
