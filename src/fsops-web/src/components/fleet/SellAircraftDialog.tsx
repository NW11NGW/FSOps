import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, Banknote, RefreshCw, Users } from 'lucide-react'
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
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useSaleQuote } from '@/hooks/useSaleQuote'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import type { FleetAircraftSummary } from '@/types/fleet'
import type { SaleResult } from '@/types/fleet'

interface SellAircraftDialogProps {
  aircraft: FleetAircraftSummary | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

/**
 * True only for the sell endpoint's own stale-quote refusal (FleetDisposalEndpoints.SellAsync),
 * which is the single case where re-quoting is the right next step - the airframe genuinely moved
 * between the quote and the click. Every other refusal - the aircraft now in flight, or on a virtual
 * pilot's standing schedule, or already sold - is a 400 with a plain `error` string and no
 * `currentSaleValue`, so checking for that field (rather than matching on the message's wording,
 * which is for display and can be reworded without warning) is what tells the two apart.
 *
 * Same shape as LoanRepaymentDialog's and EndLeaseDialog's. Re-quoting on a hard refusal replaces
 * the real reason with "this aircraft's figures changed", which is untrue and unactionable.
 */
function isStaleQuoteError(err: ApiError): boolean {
  return typeof err.body === 'object' && err.body !== null && 'currentSaleValue' in err.body
}

/**
 * Selling an owned aircraft moves real money and can't be undone, so the actual figure is shown
 * before confirming and posted as an itemised ledger line afterwards - plus the FSOps
 * project instruction that a disposal action needs an explicit confirmation naming the aircraft by
 * registration, never a bare "are you sure". GET /fleet/{id}/sale-quote is read-only and
 * side-effect-free so it's safe to poll on open; POST /fleet/{id}/sell re-validates everything the
 * quote checked server-side.
 *
 * The server carries an optimistic-concurrency guard (`SellAircraftRequest.ExpectedSaleValue` in
 * FleetDisposalEndpoints.cs): the sale value the player confirmed travels with the commit, and the
 * server refuses if the airframe's real figures moved between quote and confirm (a virtual pilot
 * can fly it in that gap). That refusal is expected behaviour, not an error, so it reads as a
 * re-quote here - see `handleSubmit`'s catch block and the "figures changed" banner below, never a
 * raw error message.
 */
export function SellAircraftDialog({ aircraft, onOpenChange, onSuccess }: SellAircraftDialogProps) {
  const { status, quote, refetch } = useSaleQuote(aircraft?.id ?? null)
  const { fmt } = useSettings()

  const [confirmText, setConfirmText] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [priceChangedFrom, setPriceChangedFrom] = useState<number | null>(null)
  const awaitingRevalidation = useRef(false)
  const submittedValue = useRef<number | null>(null)
  // The server's own words for the refusal that triggered the re-quote, so that a re-quote which
  // comes back unchanged still explains itself rather than silently redrawing the same dialog.
  const pendingStaleMessage = useRef<string | null>(null)

  // Depend on the primitive id, NOT `aircraft` itself - callers (Fleet.tsx) construct `aircraft`
  // as a fresh object literal on every render they make (e.g. from a live cash-balance heartbeat
  // elsewhere in the tree), so a reference-identity dependency here would reset confirmText on
  // every unrelated re-render - wiping out whatever the player had just typed into the "type to
  // confirm" field mid-keystroke. Same fix as EndLeaseDialog's `target?.id` dependency.
  useEffect(() => {
    setConfirmText('')
    setError(null)
    setPriceChangedFrom(null)
    awaitingRevalidation.current = false
    pendingStaleMessage.current = null
  }, [aircraft?.id])

  // Once a re-fetch triggered by a refused sell resolves, decide whether the figures actually
  // moved (show a re-quote banner) or the sale is simply blocked now for another reason (the
  // block-reason banner below already covers that from `quote.canSell`). Every branch ends with
  // the player able to tell what their click did - none of them leaves the dialog silent.
  useEffect(() => {
    if (!awaitingRevalidation.current) return
    if (status === 'error') {
      awaitingRevalidation.current = false
      setError(pendingStaleMessage.current ?? 'Could not sell this aircraft. Check your connection and try again.')
      pendingStaleMessage.current = null
      return
    }
    if (status !== 'ready') return
    awaitingRevalidation.current = false
    if (quote && quote.canSell && submittedValue.current !== null && quote.saleValue !== submittedValue.current) {
      setPriceChangedFrom(submittedValue.current)
      setError(null)
    } else if (quote && quote.canSell) {
      setError(pendingStaleMessage.current)
    }
    pendingStaleMessage.current = null
  }, [status, quote])

  // Radix keeps the content mounted while it plays the close animation, by which time `aircraft` is
  // already null - so the last frame of a successful sale would read "Sell ?" over an empty body.
  // Display only; `canSubmit` below stays keyed off the live `aircraft`/`quote`. Same fix as
  // EndLeaseDialog's.
  const lastAircraft = useRef<FleetAircraftSummary | null>(null)
  if (aircraft) lastAircraft.current = aircraft
  const shown = aircraft ?? lastAircraft.current

  const registration = shown?.registration ?? ''
  const confirmed = confirmText.trim().toUpperCase() === registration.toUpperCase() && registration.length > 0
  const canSubmit = status === 'ready' && quote !== null && quote.canSell && confirmed && !submitting

  async function handleSubmit() {
    if (!aircraft || !quote) return
    setSubmitting(true)
    setError(null)
    setPriceChangedFrom(null)
    submittedValue.current = quote.saleValue
    try {
      const result = await post<SaleResult>(`/fleet/${aircraft.id}/sell`, { expectedSaleValue: quote.saleValue })
      toast.success(`Sold ${aircraft.registration} for ${fmt.money(result.saleValue)}. Cash balance now ${fmt.money(result.cashBalance)}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      if (err instanceof ApiError && isStaleQuoteError(err)) {
        // "The figures moved since you looked" - background flying between quote and confirm. The
        // guard working as intended, so re-quote rather than reporting it as a failure.
        pendingStaleMessage.current = err.message
        awaitingRevalidation.current = true
        refetch()
      } else if (err instanceof ApiError) {
        // A hard refusal - in flight, on a pilot's standing schedule, already sold. Nothing about
        // the quote changed, so this must read as itself rather than as a price change.
        setError(err.message)
      } else {
        setError('Could not sell this aircraft. Check your connection and try again.')
      }
    } finally {
      setSubmitting(false)
    }
  }

  const depreciationReasons: string[] = []
  if (quote) {
    if (quote.resaleFactorApplied < 1) depreciationReasons.push('airframe hours and condition')
    if (quote.isGroundedForMaintenance) depreciationReasons.push('it is currently grounded for maintenance')
  }

  return (
    <Dialog open={aircraft !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Sell {registration}?</DialogTitle>
          <DialogDescription>{shown?.aircraftTypeName} &middot; this moves real money and can't be undone.</DialogDescription>
        </DialogHeader>

        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">Could not price this sale. Check your connection and try again.</p>}

        {status === 'ready' && quote && !quote.canSell && (
          <div className="space-y-3">
            <div className="flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 p-3 text-sm text-danger">
              <Users className="mt-0.5 size-4 shrink-0" />
              <span>{quote.blockReason}</span>
            </div>
          </div>
        )}

        {status === 'ready' && quote && quote.canSell && (
          <div className="space-y-3">
            {priceChangedFrom !== null && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <RefreshCw className="mt-0.5 size-4 shrink-0" />
                <span>
                  This aircraft's figures changed since you opened this dialog (it may have flown since) - the price was{' '}
                  {fmt.money(priceChangedFrom)}, it's now {fmt.money(quote.saleValue)}. Review below and confirm again.
                </span>
              </div>
            )}

            {quote.isLastAircraft && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" />
                <span>This is your last aircraft - selling it leaves your airline unable to fly until you acquire another.</span>
              </div>
            )}

            <div className="space-y-2 rounded-md border border-border p-3 text-sm">
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Condition</span>
                <span className="font-medium tabular-nums">
                  {Math.round(quote.conditionPercent)}% &middot; {Math.round(quote.airframeHours).toLocaleString()}h airframe
                </span>
              </div>
              {quote.isGroundedForMaintenance && (
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Status</span>
                  <Badge variant="danger">Grounded for maintenance</Badge>
                </div>
              )}
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">New price</span>
                <span className="tabular-nums">{fmt.money(quote.newPrice)}</span>
              </div>
              <div className="flex items-center justify-between text-xs text-muted-foreground">
                <span>Resale factor applied</span>
                <span className="tabular-nums">{Math.round(quote.resaleFactorApplied * 100)}%</span>
              </div>
              <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                <span className="font-medium">You'll receive</span>
                <span className="flex items-center gap-1.5 font-semibold tabular-nums">
                  <Banknote className="size-4 text-success" />
                  {fmt.money(quote.saleValue)}
                </span>
              </div>
              {depreciationReasons.length > 0 && (
                <p className="text-xs text-muted-foreground">
                  Below the new price because of {depreciationReasons.join(' and ')} - a worn or grounded airframe always sells for less, by design.
                </p>
              )}
            </div>

            <p className="text-xs text-muted-foreground">{quote.loanNote}</p>

            <div className="space-y-1.5">
              <Label htmlFor="sell-confirm">
                Type <span className="font-mono font-semibold text-foreground">{registration}</span> to confirm
              </Label>
              <Input
                id="sell-confirm"
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
          {status === 'ready' && quote?.canSell && (
            <Button variant="destructive" onClick={handleSubmit} disabled={!canSubmit}>
              {submitting ? 'Selling…' : `Sell for ${fmt.money(quote.saleValue)}`}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
