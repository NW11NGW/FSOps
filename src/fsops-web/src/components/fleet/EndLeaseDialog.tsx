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
 * True only for the end-lease endpoint's own stale-quote refusal (FleetDisposalEndpoints.EndLeaseAsync),
 * which is the single case where re-quoting is the right next step - the figure the player confirmed
 * has genuinely moved. Every other refusal from that endpoint - not enough cash, the aircraft having
 * gone onto a virtual pilot's standing schedule, the lease already being ended - is a 400 with a
 * plain `error` string and no `currentTotalCharge`, so checking for that field (rather than matching
 * on the message's wording, which is for display and can be reworded without warning) is what
 * actually tells the two apart.
 *
 * Same shape, and the same reason, as LoanRepaymentDialog's own isStaleQuoteError: re-quoting on a
 * hard refusal replaces the real reason with "this lease's figures changed", which is both untrue
 * and unactionable - a player short of cash is told to confirm again, does, and is refused again.
 */
function isStaleQuoteError(err: ApiError): boolean {
  return typeof err.body === 'object' && err.body !== null && 'currentTotalCharge' in err.body
}

/**
 * Ending a lease early moves real money and can't be undone: returning early has to cost something
 * (pro-rata rent for days already used, plus an early-termination fee), or leasing is a free rental
 * and the project rule that a disposal action needs an explicit confirmation naming the aircraft
 * by registration. Shared between the Fleet page (per-aircraft dispose action) and the Finances
 * page's Leases section (per-lease "end lease" action) - both just need a fleet aircraft id and
 * its registration.
 *
 * The server carries an optimistic-concurrency guard like the sell path's, but asking a different
 * question (see FleetDisposalEndpoints.EndLeaseAsync). A lease-termination charge grows with the
 * clock, so "did the number change" was a question no player could ever answer in time - it refused
 * every click, and the re-quote banner that followed looked exactly like a button that had swallowed
 * it. The guard now asks whether the lease is still in the billing period it was quoted in, which
 * only a rent payment falling due can change. That refusal reads as a re-quote here (see
 * `handleSubmit`'s catch block and the banner below); every other refusal reads as itself.
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
  // The server's own words for the refusal that triggered the re-quote. Kept so that if the re-quote
  // comes back and the charge has NOT actually moved, the click still says something rather than
  // silently redrawing the same dialog - a confirmation that appears to do nothing is the whole
  // defect this dialog was fixed for.
  const pendingStaleMessage = useRef<string | null>(null)

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
    pendingStaleMessage.current = null
  }, [target?.id])

  // Only reached after a genuine stale-quote refusal (see handleSubmit). Once the re-quote resolves,
  // say what changed - and if it somehow did not change, or could not be re-priced, still say
  // something. Every branch here ends with the player able to tell what their click did.
  useEffect(() => {
    if (!awaitingRevalidation.current) return
    if (status === 'error') {
      awaitingRevalidation.current = false
      setError(pendingStaleMessage.current ?? 'Could not end this lease. Check your connection and try again.')
      pendingStaleMessage.current = null
      return
    }
    if (status !== 'ready') return
    awaitingRevalidation.current = false
    if (quote && quote.canEndLease && submittedCharge.current !== null && quote.totalCharge !== submittedCharge.current) {
      setPriceChangedFrom(submittedCharge.current)
      setError(null)
    } else if (quote && quote.canEndLease) {
      // Refused as stale, yet the fresh quote reads the same. Nothing to re-confirm against, so the
      // server's own reason is the only honest thing to show - never an unchanged, unexplained dialog.
      setError(pendingStaleMessage.current)
    }
    // The remaining case - the re-quote came back blocked - is already covered by the blockReason
    // banner, which replaces this whole section of the dialog.
    pendingStaleMessage.current = null
  }, [status, quote])

  // Radix keeps the content mounted while it plays the close animation, and by then `target` is
  // already null - so the last frame of a SUCCESSFUL end-lease read "End lease on ?" over an empty
  // body. A degraded frame at the exact moment the action worked is the last thing this dialog
  // should show, so remember what it was about and let the exit still name the aircraft. Display
  // only: `canSubmit` below stays keyed off the live `target`/`quote`.
  const lastTarget = useRef<EndLeaseTarget | null>(null)
  if (target) lastTarget.current = target
  const shown = target ?? lastTarget.current

  const registration = shown?.registration ?? ''
  const confirmed = confirmText.trim().toUpperCase() === registration.toUpperCase() && registration.length > 0
  const canSubmit = status === 'ready' && quote !== null && quote.canEndLease && confirmed && !submitting

  async function handleSubmit() {
    if (!target || !quote) return
    setSubmitting(true)
    setError(null)
    setPriceChangedFrom(null)
    submittedCharge.current = quote.totalCharge
    try {
      // Both the figure and the billing period it was priced in travel with the commit. The server
      // charges what it recomputes, and uses the period only to answer "is this still the same
      // period you were quoted in" - see FleetDisposalEndpoints.EndLeaseAsync.
      const result = await post<LeaseTerminationResult>(`/fleet/${target.id}/end-lease`, {
        expectedTotalCharge: quote.totalCharge,
        expectedPeriodStartUtc: quote.currentPeriodStartUtc,
      })
      toast.success(`Ended the lease on ${target.registration} - charged ${fmt.money(result.totalCharge)}. Cash balance now ${fmt.money(result.cashBalance)}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      if (err instanceof ApiError && isStaleQuoteError(err)) {
        // The guard working as intended, not a failure: a billing tick landed between the quote and
        // this click. Re-quote and let the player decide again against the current figure.
        pendingStaleMessage.current = err.message
        awaitingRevalidation.current = true
        refetch()
      } else if (err instanceof ApiError) {
        // Any other refusal - not enough cash, the aircraft now on a pilot's standing schedule, the
        // lease already ended. Nothing about the quote changed, so this must never claim otherwise:
        // shown as itself, and the quote is left alone rather than re-fetched.
        setError(err.message)
      } else {
        setError('Could not end this lease. Check your connection and try again.')
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={target !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>End lease on {registration}?</DialogTitle>
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
                  A rent payment fell due while this dialog was open, so this lease has moved into a new billing period - the
                  charge was {fmt.money(priceChangedFrom)}, it's now {fmt.money(quote.totalCharge)}. Nothing has been charged.
                  Review the new figures below and confirm again.
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
