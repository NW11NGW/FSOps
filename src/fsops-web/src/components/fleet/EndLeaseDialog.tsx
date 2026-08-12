import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, RefreshCw, Users } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useLeaseTerminationQuote } from '@/hooks/useLeaseTerminationQuote'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import { formatDate } from '@/lib/format'
import type { LeaseTerminationResult } from '@/types/fleet'

interface EndLeaseTarget {
  id: string
  registration: string
}

interface EndLeaseDialogProps {
  target: EndLeaseTarget | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

/**
 * Ending a lease early moves real money and can't be undone: returning early has to cost something
 * (pro-rata rent for days already used, plus an early-termination fee), or leasing is a free rental
 * and the project rule that a disposal action needs an explicit confirmation naming the aircraft
 * by registration. Shared between the Fleet page (per-aircraft dispose action) and the Finances
 * page's Leases section (per-lease "end lease" action) - both just need a fleet aircraft id and
 * its registration.
 *
 * The server carries the same optimistic-concurrency guard as the sell path
 * (`EndLeaseRequest.ExpectedTotalCharge` in FleetDisposalEndpoints.cs): the total charge the player
 * confirmed travels with the commit, and the server refuses if the real figures moved between quote
 * and confirm. That refusal reads as a re-quote here (see `handleSubmit`'s catch block and the
 * "figures changed" banner below), never a raw error.
 */
export function EndLeaseDialog({ target, onOpenChange, onSuccess }: EndLeaseDialogProps) {
  const { status, quote, refetch } = useLeaseTerminationQuote(target?.id ?? null)
  const { fmt } = useSettings()

  const [confirmText, setConfirmText] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [priceChangedFrom, setPriceChangedFrom] = useState<number | null>(null)
  const awaitingRevalidation = useRef(false)
  const submittedCharge = useRef<number | null>(null)

  // Depend on the primitive id, NOT `target` itself - both call sites (Fleet.tsx, LeasesSection via
  // Finances.tsx) construct `target` as a fresh object literal on every render they make (e.g. from
  // a live cash-balance heartbeat elsewhere in the tree), so a reference-identity dependency here
  // would reset confirmText on every unrelated re-render - wiping out whatever the player had just
  // typed into the "type to confirm" field mid-keystroke. Found via manual testing, not a type error.
  useEffect(() => {
    setConfirmText('')
    setError(null)
    setPriceChangedFrom(null)
    awaitingRevalidation.current = false
  }, [target?.id])

  useEffect(() => {
    if (!awaitingRevalidation.current || status !== 'ready') return
    awaitingRevalidation.current = false
    if (quote && quote.canEndLease && submittedCharge.current !== null && quote.totalCharge !== submittedCharge.current) {
      setPriceChangedFrom(submittedCharge.current)
      setError(null)
    }
  }, [status, quote])

  const registration = target?.registration ?? ''
  const confirmed = confirmText.trim().toUpperCase() === registration.toUpperCase() && registration.length > 0
  const canSubmit = status === 'ready' && quote !== null && quote.canEndLease && confirmed && !submitting

  async function handleSubmit() {
    if (!target || !quote) return
    setSubmitting(true)
    setError(null)
    setPriceChangedFrom(null)
    submittedCharge.current = quote.totalCharge
    try {
      const result = await post<LeaseTerminationResult>(`/fleet/${target.id}/end-lease`, { expectedTotalCharge: quote.totalCharge })
      toast.success(`Ended the lease on ${target.registration} - charged ${fmt.money(result.totalCharge)}. Cash balance now ${fmt.money(result.cashBalance)}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not end this lease. Check your connection and try again.')
      awaitingRevalidation.current = true
      refetch()
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={target !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>End lease on {target?.registration}?</DialogTitle>
          <DialogDescription>{quote?.aircraftTypeName ?? 'This'} lease returns early - it moves real money and can't be undone.</DialogDescription>
        </DialogHeader>

        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">Could not price this early return. Check your connection and try again.</p>}

        {status === 'ready' && quote && !quote.canEndLease && (
          <div className="flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 p-3 text-sm text-danger">
            <Users className="mt-0.5 size-4 shrink-0" />
            <span>{quote.blockReason}</span>
          </div>
        )}

        {status === 'ready' && quote && quote.canEndLease && (
          <div className="space-y-3">
            {priceChangedFrom !== null && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <RefreshCw className="mt-0.5 size-4 shrink-0" />
                <span>
                  This lease's figures changed since you opened this dialog - the charge was {fmt.money(priceChangedFrom)}, it's now{' '}
                  {fmt.money(quote.totalCharge)}. Review below and confirm again.
                </span>
              </div>
            )}

            {quote.isLastAircraft && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" />
                <span>This is your last aircraft - ending this lease leaves your airline unable to fly until you acquire another.</span>
              </div>
            )}

            <div className="space-y-2 rounded-md border border-border p-3 text-sm">
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Monthly rate</span>
                <span className="tabular-nums">{fmt.money(quote.monthlyRate)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Days into current period</span>
                <span className="tabular-nums">{quote.daysIntoCurrentPeriod.toFixed(1)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Pro-rata rent owed</span>
                <span className="tabular-nums">{fmt.money(quote.proRataAmount)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Early-termination fee</span>
                <span className="tabular-nums">{fmt.money(quote.earlyTerminationFee)}</span>
              </div>
              <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                <span className="font-medium">Total charge</span>
                <span className="font-semibold tabular-nums text-danger">{fmt.money(quote.totalCharge)}</span>
              </div>
              <p className="text-xs text-muted-foreground">
                Would otherwise have next billed {formatDate(quote.nextScheduledPaymentUtc)} - ending early charges for the
                {' '}{quote.daysIntoCurrentPeriod.toFixed(1)} day(s) already used this 30-day cycle plus the early-exit fee.
              </p>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="end-lease-confirm">
                Type <span className="font-mono font-semibold text-foreground">{registration}</span> to confirm
              </Label>
              <Input
                id="end-lease-confirm"
                value={confirmText}
                onChange={(e) => setConfirmText(e.target.value.toUpperCase())}
                placeholder={registration}
                className="font-mono uppercase"
                autoComplete="off"
              />
            </div>
          </div>
        )}

        {error && priceChangedFrom === null && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          {status === 'ready' && quote?.canEndLease && (
            <Button variant="destructive" onClick={handleSubmit} disabled={!canSubmit}>
              {submitting ? 'Ending lease…' : `End lease - charge ${fmt.money(quote.totalCharge)}`}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
