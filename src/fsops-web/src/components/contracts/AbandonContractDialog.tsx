import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, Info } from 'lucide-react'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { Contract } from '@/types/contract'

interface AbandonContractDialogProps {
  contract: Contract | null
  onOpenChange: (open: boolean) => void
  /** Performs exactly one POST. Resolves once the board has been reloaded. */
  onConfirm: (contractId: string) => Promise<void>
}

/**
 * Handing a job back, confirmed once.
 *
 * <p><b>One confirmation, and one only.</b> This app has already shipped a dialog that took three
 * clicks to do one thing; the fix is not a second safety net but a first one that actually says what
 * will happen. So the charge is named on the button itself, and pressing it does the thing.</p>
 *
 * <p><b>The figure is the server's, not a local calculation.</b> `abandonCharge` and `abandonReason`
 * come down on the contract from the same call that posts the charge
 * (`ContractEconomicsPoster.QuoteAbandon`). The client deliberately does <i>not</i> multiply
 * `outstandingFee` by anything: the fraction it would need is server-side economy config it cannot
 * see, so a local sum would be right only until that number was tuned and would then quietly promise
 * something other than what happens. What the dialog says and what the ledger records are the same
 * value by construction.</p>
 *
 * <p><b>And there is no stale-quote guard, deliberately.</b> The charge depends only on which legs
 * have been flown, and a leg cannot be flown while this is open - starting one is refused while any
 * flight is in progress, and abandoning is refused while a leg is airborne. Guarding a figure that
 * cannot move is how this project previously shipped a confirmation no human could satisfy.</p>
 */
export function AbandonContractDialog({ contract, onOpenChange, onConfirm }: AbandonContractDialogProps) {
  const { fmt } = useSettings()
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Radix keeps the content mounted through the closing animation, by which time `contract` is
  // already null - so the last frame of a SUCCESSFUL abandon would otherwise read "Hand back ?" over
  // an empty body. Display only; the submit path stays keyed off the live `contract`.
  const lastContract = useRef<Contract | null>(null)
  if (contract) lastContract.current = contract
  const shown = contract ?? lastContract.current

  useEffect(() => {
    setError(null)
    setSubmitting(false)
  }, [contract?.id])

  async function handleConfirm() {
    // Belt and braces against a double click producing two charges: the button is disabled while
    // submitting, and this returns early if it is somehow re-entered anyway.
    if (!contract || submitting) return
    setSubmitting(true)
    setError(null)
    try {
      await onConfirm(contract.id)
      onOpenChange(false)
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : 'Could not hand this job back. Check your connection and try again.',
      )
      setSubmitting(false)
    }
  }

  const free = (shown?.abandonCharge ?? 0) <= 0
  const unflown = shown ? shown.legCount - shown.flownLegCount : 0

  return (
    <Dialog open={contract !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Hand this job back?</DialogTitle>
          <DialogDescription>
            {shown
              ? `${shown.operatorName} — ${shown.flownLegCount} of ${shown.legCount} legs flown.`
              : 'This job will be closed.'}
          </DialogDescription>
        </DialogHeader>

        {shown && (
          <div className="space-y-3">
            <div
              className={cn(
                'flex items-start gap-2 rounded-md border p-3 text-sm',
                free ? 'border-border bg-muted/40' : 'border-warning/30 bg-warning/10',
              )}
            >
              {free ? (
                <Info className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
              ) : (
                <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" />
              )}
              {/* The server's sentence, verbatim. It is written to be read. */}
              <span className={free ? 'text-muted-foreground' : 'text-warning'}>{shown.abandonReason}</span>
            </div>

            <div className="space-y-2 rounded-md border border-border p-3 text-sm">
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Earned from the {shown.flownLegCount} leg(s) you flew</span>
                <span className="tabular-nums text-success">{fmt.money(shown.earnedSoFar)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Value of the {unflown} leg(s) left</span>
                <span className="tabular-nums">{fmt.money(shown.outstandingFee)}</span>
              </div>
              <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                <span className="font-medium">Charge to hand back</span>
                <span className={cn('font-semibold tabular-nums', free ? 'text-success' : 'text-danger')}>
                  {free ? 'Free' : fmt.money(shown.abandonCharge)}
                </span>
              </div>
              {/* Deliberately does NOT say "it was paid as each leg landed". That is true of a leg
               *  flown in the simulator and false of one completed with estimates, which counts as
               *  flown but pays nothing - so asserting it would be wrong on exactly the sectors a
               *  player is most likely to be unsure about. What IS true in every case is that
               *  handing the job back takes nothing away retrospectively. */}
              <p className="text-xs text-muted-foreground">
                Handing the job back does not undo the legs you have already flown — the charge above is
                only for the ones left.
              </p>
            </div>

            {/* Forfeited, not billed. The bonus is not part of the charge above and must not read as
             *  though it were - but losing it is the real cost of walking away from a long chain, so
             *  it has to be said before the player commits rather than discovered afterwards. */}
            {shown.completionBonus > 0 && (
              <div className="flex items-start gap-2 rounded-md border border-border p-3 text-sm">
                <Info className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                <span className="text-muted-foreground">
                  You also give up the{' '}
                  <span className="font-medium text-foreground">{fmt.money(shown.completionBonus)}</span> bonus for
                  finishing all {shown.legCount} legs. That is not added to the charge — you simply do not earn it.
                </span>
              </div>
            )}
          </div>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Keep the job
          </Button>
          <Button variant="destructive" onClick={handleConfirm} disabled={submitting || !contract}>
            {submitting
              ? 'Handing back…'
              : free
                ? 'Hand back — free'
                : `Hand back — charge ${fmt.money(shown?.abandonCharge ?? 0)}`}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
