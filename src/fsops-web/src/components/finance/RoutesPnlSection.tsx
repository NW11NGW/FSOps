import { Map } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { FinanceRoute } from '@/types/finance'

interface RoutesPnlSectionProps {
  status: 'loading' | 'ready' | 'error'
  routes: FinanceRoute[]
  periodDays: number
}

/**
 * Profit and loss per route: which routes actually make money, given every real cost.
 * From completed, revenue-posted flights only - see FinanceRoute doc for why the field
 * names carry no "30Days" suffix (the window is whatever `periodDays` was requested for).
 */
export function RoutesPnlSection({ status, routes, periodDays }: RoutesPnlSectionProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Map className="size-4 text-muted-foreground" />
          P&amp;L per route
        </CardTitle>
        <p className="text-xs text-muted-foreground">Last {periodDays} days, from completed flights only.</p>
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load route P&amp;L. Check your connection and try again.</p>}

        {status === 'ready' && routes.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={Map} title="No completed flights yet" description="Fly a route, or wait for a virtual pilot to, and its P&L will appear here." />
          </div>
        )}

        {status === 'ready' && routes.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Route</TableHead>
                <TableHead>Sectors</TableHead>
                <TableHead>Revenue</TableHead>
                <TableHead>Cost</TableHead>
                <TableHead>Profit</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {routes.map((route) => (
                <TableRow key={route.routeId}>
                  <TableCell>
                    <p className="font-medium">
                      {route.departureIcao} &rarr; {route.arrivalIcao}
                    </p>
                    {route.flightNumber && <p className="text-xs text-muted-foreground">{route.flightNumber}</p>}
                  </TableCell>
                  <TableCell className="tabular-nums">{route.sectorsFlown}</TableCell>
                  <TableCell className="tabular-nums">{fmt.money(route.revenue)}</TableCell>
                  <TableCell className="tabular-nums">{fmt.money(route.cost)}</TableCell>
                  <TableCell className={cn('font-medium tabular-nums', route.profit >= 0 ? 'text-success' : 'text-danger')}>
                    {fmt.money(route.profit)}
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
