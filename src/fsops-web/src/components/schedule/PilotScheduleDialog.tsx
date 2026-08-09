import { ScheduleBuilder } from './ScheduleBuilder'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import type { PilotSummary } from '@/types/pilot'

interface PilotScheduleDialogProps {
  pilot: PilotSummary | null
  onOpenChange: (open: boolean) => void
  onSaved?: () => void
}

/** Hosts the weekly schedule builder in a wide dialog, opened from the Pilots roster. `pilot`
 *  being null closes the dialog - kept separate from ScheduleBuilder itself so the builder stays
 *  reusable outside a dialog if a later chunk wants a dedicated page. */
export function PilotScheduleDialog({ pilot, onOpenChange, onSaved }: PilotScheduleDialogProps) {
  return (
    <Dialog open={pilot !== null} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-6xl w-[95vw] max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{pilot ? `${pilot.name}'s weekly schedule` : 'Weekly schedule'}</DialogTitle>
          <DialogDescription>
            Build a standing weekly rotation - it repeats every week until you change it. Pick an aircraft for
            each day, then add the legs it can fly.
          </DialogDescription>
        </DialogHeader>
        {pilot && <ScheduleBuilder pilot={pilot} onSaved={onSaved} />}
      </DialogContent>
    </Dialog>
  )
}
