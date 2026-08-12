import { Badge } from '@/components/ui/badge'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import { useFlightDetailOnDemand } from '@/hooks/useFlightDetailOnDemand'
import { useSettings } from '@/hooks/useSettings'
import { formatDateTime } from '@/lib/format'
import { ledgerCategoryBadgeVariant, ledgerCategoryLabel } from '@/lib/ledgerCategory'
import { cn } from '@/lib/utils'

interface LedgerFlightDialogProps {
  flightId: string | null
  onOpenChange: (open: boolean) => void
}

/**
 * The ledger is itemised, filterable by category, and drillable to the flight that produced a
 * line. Reuses GET /flights/{id} (already used by the Fly page's report card) through
 * its own on-demand hook - see useFlightDetailOnDemand's doc comment for why the fetch is deferred
 * until a row is actually opened.
 */
export function LedgerFlightDialog({ flightId, onOpenChange }: LedgerFlightDialogProps) {
  const { status, data, errorMessage } = useFlightDetailOnDemand(flightId)
  const { fmt } = useSettings()

  return (
    <Dialog open={flightId !== null} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Flight detail</DialogTitle>
          <DialogDescription>The ledger lines this flight produced.</DialogDescription>
        </DialogHeader>

        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-16 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">{errorMessage}</p>}

        {status === 'ready' && data && (
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 rounded-md border border-border p-3 text-sm">
              <div className="text-muted-foreground">Status</div>
              <div className="text-right">{data.flight.status}</div>
              <div className="text-muted-foreground">Departed</div>
              <div className="text-right">{formatDateTime(data.flight.outUtc)}</div>
              <div className="text-muted-foreground">Arrived</div>
              <div className="text-right">{formatDateTime(data.flight.inUtc)}</div>
              <div className="text-muted-foreground">Revenue</div>
              <div className="text-right font-medium tabular-nums text-success">{fmt.money(data.flight.revenue)}</div>
              <div className="text-muted-foreground">Total cost</div>
              <div className="text-right font-medium tabular-nums text-danger">{fmt.money(data.flight.totalCost)}</div>
            </div>

            <Separator />

            <div className="space-y-2">
              <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Ledger lines from this flight</p>
              {data.ledgerTransactions.length === 0 && <p className="text-sm text-muted-foreground">No ledger lines posted for this flight yet.</p>}
              {data.ledgerTransactions.map((line) => (
                <div key={line.id} className="flex items-center justify-between gap-3 text-sm">
                  <div className="min-w-0">
                    <div className="flex items-center gap-1.5">
                      <Badge variant={ledgerCategoryBadgeVariant(line.category)}>{ledgerCategoryLabel(line.category)}</Badge>
                    </div>
                    <p className="mt-0.5 min-w-0 truncate text-xs text-muted-foreground">{line.description}</p>
                  </div>
                  <span className={cn('shrink-0 font-medium tabular-nums', line.amount >= 0 ? 'text-success' : 'text-danger')}>
                    {fmt.money(line.amount)}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
