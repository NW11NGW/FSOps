import { useEffect, useState } from 'react'
import { toast } from 'sonner'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useMaintenanceQuote } from '@/hooks/useMaintenanceQuote'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import type { MaintenanceCheckType, PerformMaintenanceResult } from '@/types/maintenance'

interface PerformMaintenanceDialogProps {
  /** The aircraft to quote/perform maintenance on, or null to keep the dialog closed. */
  fleetAircraftId: string | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

/**
 * "Perform maintenance now":
 * bring an A- or C-check forward at the player's own choosing, instead of waiting for it to fall
 * due mid-week. States the trade-off plainly before confirming - full cost, full downtime, and any
 * hours already accrued on the current cycle are forfeited, not pro-rated - plus which pilots'
 * schedules will be affected. Blocked outright while the aircraft is airborne or already grounded.
 * <para>
 * Same quote-then-confirm shape as LoanRepaymentDialog/SellAircraftDialog: the quote is recomputed
 * server-side at commit time and compared against what was shown, since a background virtual-pilot
 * flight can move or ground the aircraft between opening this dialog and clicking confirm.
 * </para>
 */
export function PerformMaintenanceDialog({ fleetAircraftId, onOpenChange, onSuccess }: PerformMaintenanceDialogProps) {
  const { fmt } = useSettings()
  const [selectedType, setSelectedType] = useState<MaintenanceCheckType>('ACheck')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const { status, quote, refetch } = useMaintenanceQuote(fleetAircraftId)

  useEffect(() => {
    setSelectedType('ACheck')
    setError(null)
  }, [fleetAircraftId])

  const selectedQuote = quote?.quotes.find((q) => q.type === selectedType) ?? null

  async function handlePerform() {
    if (!fleetAircraftId || !selectedQuote) return
    setSubmitting(true)
    setError(null)
    try {
      const result = await post<PerformMaintenanceResult>(`/fleet/${fleetAircraftId}/perform-maintenance`, {
        type: selectedQuote.type,
        expectedCost: selectedQuote.cost,
        expectedDowntimeHours: selectedQuote.downtimeHours,
      })
      toast.success(
        `${result.registration} grounded for ${result.type === 'CCheck' ? 'a C-check' : 'an A-check'} - ${fmt.money(result.cost)} charged.`,
      )
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      if (err instanceof ApiError && err.status === 400) {
        // A quote-drift refusal (or a status the quote no longer reflects) - re-fetch so the
        // dialog shows the current, correct figures rather than leaving a stale one on screen.
        refetch()
      }
      setError(err instanceof ApiError ? err.message : 'Could not perform maintenance. Check your connection and try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={fleetAircraftId !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Perform maintenance now</DialogTitle>
          <DialogDescription>
            {quote ? `${quote.registration} - bring a check forward at a moment of your choosing.` : 'Bring a check forward at a moment of your choosing.'}
          </DialogDescription>
        </DialogHeader>

        {status === 'loading' && <Skeleton className="h-40 w-full" />}
        {status === 'error' && <p className="text-sm text-danger">Could not load this aircraft's maintenance status. Check your connection and try again.</p>}

        {status === 'ready' && quote && !quote.canPerform && (
          <p className="rounded-md border border-border bg-muted p-3 text-sm text-muted-foreground">{quote.blockReason}</p>
        )}

        {status === 'ready' && quote && quote.canPerform && (
          <div className="space-y-3">
            <Tabs value={selectedType} onValueChange={(v) => setSelectedType(v as MaintenanceCheckType)}>
              <TabsList>
                <TabsTrigger value="ACheck">A-check</TabsTrigger>
                <TabsTrigger value="CCheck">C-check</TabsTrigger>
              </TabsList>
            </Tabs>

            {selectedQuote && (
              <div className="space-y-2 rounded-md border border-border p-3 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Hours since last check</span>
                  <span className="tabular-nums">{selectedQuote.hoursSinceCheck.toFixed(0)}h</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Would fall due naturally in</span>
                  <span className="tabular-nums">{selectedQuote.hoursRemainingUntilNatural.toFixed(0)}h</span>
                </div>
                <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                  <span className="font-medium">Cost</span>
                  <span className="font-semibold tabular-nums">{fmt.money(selectedQuote.cost)}</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Downtime</span>
                  <span className="tabular-nums">{selectedQuote.downtimeHours.toFixed(0)}h</span>
                </div>
                <p className="text-xs text-muted-foreground">
                  Full cost, full downtime - the {selectedQuote.hoursRemainingUntilNatural.toFixed(0)} hours remaining on this cycle are forfeited, not
                  refunded. Bringing a check forward is never cheaper than letting it fall due.
                </p>
              </div>
            )}

            {quote.affectedSchedules.length > 0 && (
              <div className="space-y-1.5">
                <p className="text-xs font-medium text-muted-foreground">Affects {quote.affectedSchedules.length} scheduled leg(s)</p>
                <div className="flex flex-wrap gap-1.5">
                  {quote.affectedSchedules.map((entry, i) => (
                    <Badge key={i} variant="muted">
                      {entry.pilotName}: {entry.dayOfWeek} {entry.departureTimeUtc} {entry.routeLabel}
                    </Badge>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button
            onClick={handlePerform}
            disabled={submitting || status !== 'ready' || !quote?.canPerform || !selectedQuote}
            variant="destructive"
          >
            {submitting
              ? 'Grounding…'
              : quote?.canPerform && selectedQuote
                ? `Ground now - ${fmt.money(selectedQuote.cost)}`
                : 'Perform now'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
