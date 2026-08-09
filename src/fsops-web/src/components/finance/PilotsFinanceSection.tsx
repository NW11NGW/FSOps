import { Info, Users } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { FinancePilot } from '@/types/finance'

interface PilotsFinanceSectionProps {
  status: 'loading' | 'ready' | 'error'
  pilots: FinancePilot[]
  periodDays: number
}

const ESTIMATE_EXPLANATION =
  "Adds this pilot's monthly salary, prorated to the period shown - not a posted ledger line, since the real salary charge always posts in full on your billing cycle. Useful for comparing pilots; for the literal cash that moved, check the Ledger tab."

/**
 * docs/PLAN.md "Virtual pilots - are they worth it?": salary, sectors flown, revenue against total
 * cost, and the resulting net contribution, per pilot - direct enough to answer "should I keep this
 * pilot" without the player doing arithmetic across two tables. Trailing REAL days (independent of
 * the airline's own 30-day billing cycle) - the window follows `periodDays`, so this is never
 * hardcoded to "30" or labelled "monthly". `estimatedTotalCost`/`estimatedNetContribution` are
 * explicitly estimates (prorated salary, never a ledger posting) - see FinancePilot's doc in
 * types/finance.ts - so both are labelled "Est." with a tooltip here, never presented as cash that
 * moved.
 */
export function PilotsFinanceSection({ status, pilots, periodDays }: PilotsFinanceSectionProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Users className="size-4 text-muted-foreground" />
          Virtual pilots
        </CardTitle>
        <p className="text-xs text-muted-foreground">Revenue and cost over the last {periodDays} real days, regardless of your billing cycle.</p>
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load pilot finances. Check your connection and try again.</p>}

        {status === 'ready' && pilots.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={Users} title="No pilots yet" description="Hire a virtual pilot from the Pilots page to see their cost and revenue here." />
          </div>
        )}

        {status === 'ready' && pilots.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Pilot</TableHead>
                <TableHead>Monthly salary</TableHead>
                <TableHead>Sectors ({periodDays}d)</TableHead>
                <TableHead>Revenue ({periodDays}d)</TableHead>
                <TableHead>
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span className="inline-flex items-center gap-1">
                        Est. total cost ({periodDays}d)
                        <Info className="size-3" />
                      </span>
                    </TooltipTrigger>
                    <TooltipContent className="max-w-xs">{ESTIMATE_EXPLANATION}</TooltipContent>
                  </Tooltip>
                </TableHead>
                <TableHead>
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span className="inline-flex items-center gap-1">
                        Est. net contribution ({periodDays}d)
                        <Info className="size-3" />
                      </span>
                    </TooltipTrigger>
                    <TooltipContent className="max-w-xs">{ESTIMATE_EXPLANATION}</TooltipContent>
                  </Tooltip>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {pilots.map((pilot) => (
                <TableRow key={pilot.pilotId}>
                  <TableCell>
                    <p className="font-medium">{pilot.name}</p>
                    {pilot.isPlayer && (
                      <Badge variant="muted" className="mt-0.5">
                        You
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell className="tabular-nums">{fmt.money(pilot.monthlySalary)}</TableCell>
                  <TableCell className="tabular-nums">{pilot.sectorsFlown}</TableCell>
                  <TableCell className="tabular-nums">{fmt.money(pilot.revenue)}</TableCell>
                  <TableCell className="tabular-nums">{fmt.money(pilot.estimatedTotalCost)}</TableCell>
                  <TableCell
                    className={cn(
                      'font-medium tabular-nums',
                      pilot.estimatedNetContribution > 0
                        ? 'text-success'
                        : pilot.estimatedNetContribution < 0
                          ? 'text-danger'
                          : 'text-muted-foreground',
                    )}
                  >
                    {fmt.money(pilot.estimatedNetContribution)}
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
