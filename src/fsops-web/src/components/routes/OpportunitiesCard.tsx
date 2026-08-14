import { Compass, Plus, TriangleAlert } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import type { PlanningStatus } from '@/hooks/usePlanning'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { OpportunitiesResponse, RouteOpportunity } from '@/types/planning'

interface OpportunitiesCardProps {
  data: OpportunitiesResponse | null
  status: PlanningStatus
  isRefreshing: boolean
  /** Loads the pair into the planner above so the player can look at it properly before creating
   *  it - never a one-click "create this route", which would be exactly the black box this feature
   *  exists to remove. */
  onPlan: (opportunity: RouteOpportunity) => void
}

/**
 * "Where should I fly next" - ranked city pairs the airline could open, priced with the aircraft
 * that would actually fly them, each with the reason it is being suggested.
 *
 * Pairs the fleet cannot reach are listed too, plainly, rather than quietly dropped: the same
 * spirit as route creation's own refusals, which name the aircraft and the one action that changes
 * the answer instead of leaving a dead end.
 */
export function OpportunitiesCard({ data, status, isRefreshing, onPlan }: OpportunitiesCardProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Where to fly next</CardTitle>
        {data && data.bases.length > 0 && (
          <p className="text-sm text-muted-foreground">
            Best unserved city pairs from {data.bases.slice(0, 3).join(', ')}
            {data.bases.length > 3 ? ` and ${data.bases.length - 3} more` : ''}, ranked by profit a sector.
          </p>
        )}
      </CardHeader>
      <CardContent className={cn('space-y-4 transition-opacity duration-200', isRefreshing && 'opacity-60')}>
        {status === 'loading' && !data && (
          <div className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && !data && (
          <p className="text-sm text-danger">Could not work out any suggestions. Check your connection and try again.</p>
        )}

        {data && data.opportunities.length === 0 && data.blocked.length === 0 && (
          <EmptyState
            icon={Compass}
            title="No suggestions yet"
            description={
              data.fleetTypeCount === 0
                ? 'Lease or buy an aircraft first — until then there is nothing to work out what you could fly.'
                : 'Every worthwhile pair within reach of your bases is already on your route list.'
            }
          />
        )}

        {data && data.opportunities.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Pair</TableHead>
                <TableHead>Distance</TableHead>
                <TableHead>Load</TableHead>
                <TableHead>Profit / sector</TableHead>
                <TableHead className="w-10">
                  <span className="sr-only">Plan</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.opportunities.map((opportunity) => (
                <TableRow key={`${opportunity.departureIcao}-${opportunity.arrivalIcao}`}>
                  <TableCell>
                    <div className="flex items-center gap-1.5">
                      <span className="font-mono font-medium">{opportunity.departureIcao}</span>
                      <span className="text-muted-foreground">→</span>
                      <span className="font-mono font-medium">{opportunity.arrivalIcao}</span>
                      <Badge variant="outline">{opportunity.aircraftTypeName}</Badge>
                    </div>
                    {/* The reason arrives from the server without any money in it - currency is a
                     *  user setting, so every figure is formatted here instead. */}
                    <p className="mt-0.5 max-w-prose text-xs text-muted-foreground">
                      {opportunity.reason} Suggested fare {fmt.money(opportunity.suggestedFare)}.
                    </p>
                  </TableCell>
                  <TableCell className="tabular-nums">{fmt.distance(opportunity.distanceNm)}</TableCell>
                  <TableCell className="tabular-nums">
                    {opportunity.expectedPassengers}/{opportunity.seats}
                    <span className="ml-1 text-xs text-muted-foreground">
                      ({opportunity.loadFactorPercent.toFixed(0)}%)
                    </span>
                  </TableCell>
                  <TableCell
                    className={cn('tabular-nums font-medium', opportunity.profitPerSector >= 0 ? 'text-success' : 'text-danger')}
                  >
                    {fmt.money(opportunity.profitPerSector)}
                  </TableCell>
                  <TableCell>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="size-8 text-muted-foreground"
                      aria-label={`Plan ${opportunity.departureIcao} to ${opportunity.arrivalIcao}`}
                      onClick={() => onPlan(opportunity)}
                    >
                      <Plus className="size-4" />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}

        {data && data.blocked.length > 0 && (
          <div className="space-y-1.5 rounded-md border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
            <p className="font-medium">Worth having, but nothing you own can fly it</p>
            {data.blocked.map((entry) => (
              <div key={`${entry.departureIcao}-${entry.arrivalIcao}`} className="flex items-start gap-2">
                <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />
                <p>{entry.reason}</p>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
