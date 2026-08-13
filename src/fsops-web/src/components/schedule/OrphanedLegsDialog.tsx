import { TriangleAlert } from 'lucide-react'

import type { DraftLeg, OrphanedLeg } from './draftEntry'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DAY_LABELS, type DayOfWeek } from '@/types/schedule'

export interface PendingRemoval {
  day: DayOfWeek
  leg: DraftLeg
  orphans: OrphanedLeg[]
}

interface OrphanedLegsDialogProps {
  pending: PendingRemoval | null
  onCancel: () => void
  onConfirm: () => void
}

/**
 * Shown the moment the player asks to remove a leg that would strand at least one OTHER leg on the
 * same aircraft elsewhere in the week - real-use defect, 2026-08-13: a player deleted only the
 * outbound half of an out-and-back, the return sat in the grid looking perfectly scheduled, and the
 * refusal only arrived at Save, worded around "the first leg of the week" with no mention of the
 * delete that actually caused it. `findLegsOrphanedByRemoval` (draftEntry.ts) is what works out the
 * consequence - this component only ever renders what it was handed, never recomputes anything -
 * and it walks the WHOLE chain, so a leg two or three positions downstream that only looks fine on
 * paper is named here too, not just the leg immediately next to the one being removed.
 *
 * Same rule as everywhere else in this feature: say what will happen and let the player choose.
 * Cancelling here leaves the draft exactly as it was - nothing is removed, not even the leg the
 * player originally clicked "Remove" on. Confirming removes that leg AND every leg listed here, in
 * one action (`removeLegAndOrphans`) - deliberately, not a silent cascade the player didn't ask for:
 * they are looking at the complete, named list of what this does before they agree to it, so
 * finishing the job on confirmation is completing the exact thing they just said yes to, not
 * springing a surprise addition on them. The alternative - removing only the requested leg and
 * leaving the rest flagged for the player to delete by hand - would just hand back a draft that is
 * guaranteed to fail at save for a reason already spelled out on this screen, which is a worse
 * outcome than finishing what was just agreed to.
 */
export function OrphanedLegsDialog({ pending, onCancel, onConfirm }: OrphanedLegsDialogProps) {
  const orphans = pending?.orphans ?? []
  const totalLegsRemoved = orphans.length + 1

  return (
    <Dialog open={pending !== null} onOpenChange={(open) => !open && onCancel()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>This also strands {orphans.length === 1 ? 'another leg' : `${orphans.length} other legs`}</DialogTitle>
          <DialogDescription>
            Without {pending?.leg.flightNumber ?? 'this leg'}, the aircraft never gets to where{' '}
            {orphans.length === 1 ? 'this leg departs from' : 'these legs depart from'} - {orphans.length === 1 ? 'it' : 'they'} can no
            longer be flown as scheduled.
          </DialogDescription>
        </DialogHeader>

        <ul className="space-y-2">
          {orphans.map((orphan) => (
            <li
              key={`${orphan.day}-${orphan.leg.id}`}
              className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-2.5 text-sm text-warning"
            >
              <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">
                {DAY_LABELS[orphan.day]} {orphan.leg.departureTimeUtc.slice(0, 5)}{' '}
                <span className="font-mono">
                  {orphan.leg.departureIcao} → {orphan.leg.arrivalIcao}
                </span>
                {orphan.leg.flightNumber && ` (${orphan.leg.flightNumber})`} - stuck at {orphan.aircraftActuallyAt} instead.
              </span>
            </li>
          ))}
        </ul>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" onClick={onConfirm}>
            Remove {totalLegsRemoved} legs
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
