import { useEffect, useId, useState } from 'react'
import { ChevronDown, Lightbulb, TriangleAlert, Users } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import type { PlanningStatus } from '@/hooks/usePlanning'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { FleetAdviceResponse } from '@/types/planning'

const STORAGE_KEY = 'fsops-fleet-advice-expanded'

/**
 * Whether the fleet planner is open. **Collapsed by default**, including for a brand-new player
 * with nothing stored yet - the Fleet page is about the aircraft you have, and a full-height
 * shopping list underneath it was drowning that out.
 *
 * Note which way round this is stored, and why. Its siblings on the live map
 * (`fsops-liveops-empty-collapsed`, `fsops-liveops-legend-collapsed`) default to OPEN, so they
 * store the collapse. This one defaults to CLOSED, so it stores the expansion - the same shape as
 * `vatsimAtcVisibility`'s default-off toggle. Storing it this way round means "nothing stored"
 * falls out as collapsed on its own, rather than depending on an inverted read that a later change
 * could quietly get backwards. A missing key, a malformed value, or `localStorage` throwing (a
 * locked-down embed, a privacy-restricted browser, the MSFS in-game panel) all resolve to
 * collapsed.
 */
function readExpanded(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

function writeExpanded(value: boolean): void {
  try {
    if (typeof window === 'undefined') return
    if (value) {
      window.localStorage.setItem(STORAGE_KEY, 'true')
    } else {
      window.localStorage.removeItem(STORAGE_KEY)
    }
  } catch {
    // Best-effort only - never let a locked-down storage break the Fleet page.
  }
}

/**
 * The one line the collapsed header carries, so it is worth its space rather than being an inert
 * title. Ordered by what should actually change the player's mind, not by what the card renders
 * first: "you already have aircraft doing nothing" is a reason NOT to spend, and it outranks any
 * number of suggestions to spend.
 */
function summarise(data: FleetAdviceResponse | null, status: PlanningStatus): string {
  if (!data) {
    if (status === 'loading') return 'Working it out…'
    if (status === 'error') return 'Advice unavailable'
    return 'Nothing to add yet'
  }
  if (data.idleAircraftCount > 0) {
    return data.idleAircraftCount === 1 ? '1 aircraft idle' : `${data.idleAircraftCount} aircraft idle`
  }
  if (data.unflyableRoutes.length > 0) {
    return data.unflyableRoutes.length === 1 ? '1 route you can’t fly' : `${data.unflyableRoutes.length} routes you can’t fly`
  }
  if (data.seatCappedRoutes.length > 0) {
    return data.seatCappedRoutes.length === 1
      ? '1 route turning passengers away'
      : `${data.seatCappedRoutes.length} routes turning passengers away`
  }
  if (data.suggestions.length > 0) {
    return data.suggestions.length === 1 ? '1 aircraft suggested' : `${data.suggestions.length} aircraft suggested`
  }
  return 'Nothing to add right now'
}

interface FleetAdviceCardProps {
  data: FleetAdviceResponse | null
  status: PlanningStatus
  isRefreshing: boolean
  /** Opens the buy/lease dialog - the advice says what and why, the existing flow does the buying. */
  onAcquire: () => void
}

/**
 * "What should I buy next" - grounded in what the fleet is actually doing, not in a generic
 * upgrade ladder. It will happily say "nothing": an airline with aircraft sitting idle gains more
 * from rostering them than from another airframe, and a planner that always finds a reason to spend
 * money is not advice.
 *
 * Prices come from the economy config's own sanctioned paths (purchase multiplier and the per-type
 * lease rate table), never from the catalogue row's raw columns - see EconomyConfig.LeaseRates.
 *
 * Collapsible, and collapsed by default: expanded it is the tallest thing on the Fleet page, and
 * the page is meant to be about the aircraft you already have. Collapsing it to nothing but a title
 * would have made it inert, and a feature nobody ever opens is a feature nobody has - so the
 * collapsed header still names itself, carries the single most useful fact it knows (see
 * `summarise`), and is one obvious click from opening.
 */
export function FleetAdviceCard({ data, status, isRefreshing, onAcquire }: FleetAdviceCardProps) {
  const { fmt } = useSettings()
  const [expanded, setExpanded] = useState(readExpanded)
  const contentId = useId()

  useEffect(() => {
    writeExpanded(expanded)
  }, [expanded])

  return (
    <Card>
      <CardHeader className="p-0">
        <button
          type="button"
          onClick={() => setExpanded((open) => !open)}
          aria-expanded={expanded}
          aria-controls={contentId}
          className="flex w-full items-start gap-3 rounded-lg p-6 text-left transition-colors hover:bg-muted/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <div className="min-w-0 flex-1 space-y-1.5">
            <CardTitle className="text-base">What to buy next</CardTitle>
            {/* Expanded, the server's full headline sentence; collapsed, the short summary - the
             *  header should never be the only thing on screen with nothing to say. */}
            <p className="text-sm text-muted-foreground">{expanded && data ? data.headline : summarise(data, status)}</p>
          </div>
          <span className="shrink-0 pt-0.5 text-muted-foreground">
            <ChevronDown className={cn('size-4 transition-transform duration-200', expanded && 'rotate-180')} aria-hidden="true" />
          </span>
        </button>
      </CardHeader>
      <CardContent
        id={contentId}
        hidden={!expanded}
        className={cn('space-y-4 transition-opacity duration-200', isRefreshing && 'opacity-60')}
      >
        {status === 'loading' && !data && (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && !data && (
          <p className="text-sm text-danger">Could not work out any fleet advice. Check your connection and try again.</p>
        )}

        {data && data.idleAircraftCount > 0 && (
          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
            <Users className="mt-0.5 size-4 shrink-0" />
            <div className="space-y-1">
              <p className="font-medium">
                {data.idleAircraftCount} aircraft with nothing scheduled
              </p>
              <p>
                {data.utilisation
                  .filter((a) => a.scheduledSectorsPerWeek === 0 && !a.reservedForPlayer)
                  .map((a) => `${a.registration} at ${a.locationIcao || 'an unknown airport'}`)
                  .join(', ')}
                . Roster them to a virtual pilot on the Pilots page before adding capacity.
              </p>
            </div>
          </div>
        )}

        {data && data.unflyableRoutes.length > 0 && (
          <div className="space-y-1.5 rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
            <p className="font-medium">Routes nothing you own can fly</p>
            {data.unflyableRoutes.map((route) => (
              <div key={route.routeId} className="flex items-start gap-2">
                <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />
                <p>
                  <span className="font-mono">{route.departureIcao} → {route.arrivalIcao}</span>
                  {route.reason ? ` — ${route.reason}` : ''}
                </p>
              </div>
            ))}
          </div>
        )}

        {data && data.seatCappedRoutes.length > 0 && (
          <div className="space-y-1 rounded-md border border-border bg-muted/20 p-3 text-sm">
            <p className="font-medium">Routes turning passengers away</p>
            {data.seatCappedRoutes.map((route) => (
              <p key={route.routeId} className="text-muted-foreground">
                <span className="font-mono text-foreground">{route.departureIcao} → {route.arrivalIcao}</span> — about{' '}
                {route.marketDemandPax} want to fly it, the {route.typeName} seats {route.seats}, so roughly{' '}
                {route.turnedAwayPerSector} a sector go unsold.
              </p>
            ))}
          </div>
        )}

        {data && data.suggestions.length > 0 && (
          <ul className="space-y-2">
            {data.suggestions.map((suggestion) => (
              <li key={suggestion.aircraftTypeId} className="rounded-md border border-border p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <Lightbulb className="size-4 text-accent" />
                  <span className="font-medium">{suggestion.typeName}</span>
                  <Badge variant="outline" className="font-mono">{suggestion.icaoType}</Badge>
                  {suggestion.alreadyOwned && <Badge variant="muted">Already in your fleet</Badge>}
                </div>
                {/* Same rule as the opportunity list: the server's sentence carries no money, so
                 *  the profit figure is formatted here in the player's own currency. */}
                <p className="mt-1 text-sm text-muted-foreground">
                  {suggestion.reason}
                  {suggestion.bestSector ? ` That sector would net about ${fmt.money(suggestion.bestSectorProfit)}.` : ''}
                </p>
                <dl className="mt-2 grid grid-cols-[repeat(auto-fit,minmax(9rem,1fr))] gap-x-6 gap-y-1 text-xs">
                  <div className="flex justify-between gap-2">
                    <dt className="text-muted-foreground">Buy</dt>
                    <dd className={cn('tabular-nums', suggestion.affordableToBuyNow ? 'text-success' : 'text-muted-foreground')}>
                      {fmt.money(suggestion.purchasePrice)}
                    </dd>
                  </div>
                  <div className="flex justify-between gap-2">
                    <dt className="text-muted-foreground">Lease</dt>
                    <dd className="tabular-nums">
                      {suggestion.monthlyLease === null ? 'Not leasable' : `${fmt.money(suggestion.monthlyLease)}/mo`}
                    </dd>
                  </div>
                  <div className="flex justify-between gap-2">
                    <dt className="text-muted-foreground">Lease deposit</dt>
                    <dd
                      className={cn(
                        'tabular-nums',
                        suggestion.leaseDeposit !== null && suggestion.affordableToLeaseNow ? 'text-success' : 'text-muted-foreground',
                      )}
                    >
                      {suggestion.leaseDeposit === null ? '—' : fmt.money(suggestion.leaseDeposit)}
                    </dd>
                  </div>
                  <div className="flex justify-between gap-2">
                    <dt className="text-muted-foreground">Insurance</dt>
                    <dd className="tabular-nums">{fmt.money(suggestion.monthlyInsurance)}/mo</dd>
                  </div>
                </dl>
              </li>
            ))}
          </ul>
        )}

        {data && data.suggestions.length > 0 && (
          <div className="flex justify-end">
            <Button type="button" variant="outline" size="sm" onClick={onAcquire}>
              Buy or lease an aircraft
            </Button>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
