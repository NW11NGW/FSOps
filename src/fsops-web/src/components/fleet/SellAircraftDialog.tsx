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
 * Selling an owned aircraft moves real money and can't be undone - docs/PLAN.md "Show the actual
 * figure ... before confirming, and post it as an itemised ledger line afterwards" plus the FSOps
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

  useEffect(() => {
    setConfirmText('')
    setError(null)
    setPriceChangedFrom(null)
    awaitingRevalidation.current = false
  }, [aircraft])

  // Once a re-fetch triggered by a refused sell resolves, decide whether the figures actually
  // moved (show a re-quote banner) or the sale is simply blocked now for another reason (the
  // block-reason banner below already covers that from `quote.canSell`).
  useEffect(() => {
    if (!awaitingRevalidation.current || status !== 'ready') return
    awaitingRevalidation.current = false
    if (quote && quote.canSell && submittedValue.current !== null && quote.saleValue !== submittedValue.current) {
      setPriceChangedFrom(submittedValue.current)
      setError(null)
    }
  }, [status, quote])

  const registration = aircraft?.registration ?? ''
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
      // A refusal here can legitimately mean "the figures moved since you looked" (background
      // flying between quote and confirm) rather than a hard failure - re-quote instead of just
      // reporting the error text.
      setError(err instanceof ApiError ? err.message : 'Could not sell this aircraft. Check your connection and try again.')
      awaitingRevalidation.current = true
      refetch()
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
          <DialogTitle>Sell {aircraft?.registration}?</DialogTitle>
          <DialogDescription>{aircraft?.aircraftTypeName} &middot; this moves real money and can't be undone.</DialogDescription>
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
