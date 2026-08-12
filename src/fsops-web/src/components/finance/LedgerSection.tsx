import { useState } from 'react'
import { ChevronLeft, ChevronRight, PlaneTakeoff, Receipt } from 'lucide-react'

import { LedgerFlightDialog } from '@/components/finance/LedgerFlightDialog'
import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useFinanceLedger } from '@/hooks/useFinanceLedger'
import { useSettings } from '@/hooks/useSettings'
import { formatDateTime } from '@/lib/format'
import { LEDGER_CATEGORIES, ledgerCategoryBadgeVariant, ledgerCategoryLabel } from '@/lib/ledgerCategory'
import { cn } from '@/lib/utils'

const PAGE_SIZE = 25

/**
 * The ledger is itemised, filterable by category, and drillable to the flight that produced a
 * line. Everything shown here IS a posted LedgerTransaction row, never a
 * recomputation, so nothing here can disagree with the cash balance shown elsewhere on the page.
 */
export function LedgerSection() {
  const { fmt } = useSettings()
  const [category, setCategory] = useState<string | null>(null)
  const [skip, setSkip] = useState(0)
  const [drilldownFlightId, setDrilldownFlightId] = useState<string | null>(null)

  const { status, transactions, total } = useFinanceLedger(category, skip, PAGE_SIZE)

  function handleCategoryChange(next: string) {
    setCategory(next === 'All' ? null : next)
    setSkip(0)
  }

  const rangeStart = total === 0 ? 0 : skip + 1
  const rangeEnd = Math.min(skip + PAGE_SIZE, total)

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between space-y-0 pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Receipt className="size-4 text-muted-foreground" />
          Ledger
        </CardTitle>
        <select
          value={category ?? 'All'}
          onChange={(e) => handleCategoryChange(e.target.value)}
          aria-label="Filter by category"
          className="h-9 rounded-md border border-input bg-surface px-3 text-sm shadow-elevation-1"
        >
          <option value="All">All categories</option>
          {LEDGER_CATEGORIES.map((c) => (
            <option key={c} value={c}>
              {ledgerCategoryLabel(c)}
            </option>
          ))}
        </select>
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load the ledger. Check your connection and try again.</p>}

        {status === 'ready' && transactions.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState
              icon={Receipt}
              title="Nothing posted here yet"
              description={category ? `No ${ledgerCategoryLabel(category).toLowerCase()} lines yet.` : 'Ledger lines appear as your airline trades.'}
            />
          </div>
        )}

        {status === 'ready' && transactions.length > 0 && (
          <>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Date</TableHead>
                  <TableHead>Category</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead className="text-right">Amount</TableHead>
                  <TableHead className="w-10">
                    <span className="sr-only">Flight</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {transactions.map((tx) => (
                  <TableRow key={tx.id}>
                    <TableCell className="whitespace-nowrap text-sm text-muted-foreground">{formatDateTime(tx.utc)}</TableCell>
                    <TableCell>
                      <Badge variant={ledgerCategoryBadgeVariant(tx.category)}>{ledgerCategoryLabel(tx.category)}</Badge>
                    </TableCell>
                    <TableCell className="max-w-[320px] truncate text-sm">{tx.description}</TableCell>
                    <TableCell className={cn('text-right font-medium tabular-nums', tx.amount >= 0 ? 'text-success' : 'text-danger')}>
                      {fmt.money(tx.amount)}
                    </TableCell>
                    <TableCell>
                      {tx.flightId && (
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-7"
                          aria-label="View the flight this line came from"
                          onClick={() => setDrilldownFlightId(tx.flightId)}
                        >
                          <PlaneTakeoff className="size-3.5" />
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>

            <div className="flex items-center justify-between border-t border-border px-4 py-3 text-xs text-muted-foreground">
              <span>
                {rangeStart}-{rangeEnd} of {total}
              </span>
              <div className="flex items-center gap-1">
                {/* Named, not just drawn: these are the only two controls on the page whose whole
                 *  meaning lives in an icon, so without a label a screen reader announces nothing
                 *  but "button" twice and there is no way to tell back from forward. */}
                <Button
                  type="button"
                  variant="outline"
                  size="icon"
                  className="size-7"
                  aria-label="Previous page"
                  disabled={skip === 0}
                  onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}
                >
                  <ChevronLeft className="size-3.5" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="icon"
                  className="size-7"
                  aria-label="Next page"
                  disabled={skip + PAGE_SIZE >= total}
                  onClick={() => setSkip(skip + PAGE_SIZE)}
                >
                  <ChevronRight className="size-3.5" />
                </Button>
              </div>
            </div>
          </>
        )}
      </CardContent>

      <LedgerFlightDialog flightId={drilldownFlightId} onOpenChange={(open) => !open && setDrilldownFlightId(null)} />
    </Card>
  )
}
