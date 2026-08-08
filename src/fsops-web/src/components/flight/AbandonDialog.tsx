import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

interface AbandonDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: () => Promise<void>
}

/** Confirmation gate for abandoning the in-progress flight - an irreversible, unrecoverable action. */
export function AbandonDialog({ open, onOpenChange, onConfirm }: AbandonDialogProps) {
  const [busy, setBusy] = useState(false)

  async function handleConfirm() {
    setBusy(true)
    try {
      await onConfirm()
      onOpenChange(false)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Abandon this flight?</DialogTitle>
          <DialogDescription>
            Tracking stops immediately and the flight is recorded as abandoned — no landing quality or report card, and
            your aircraft is freed up for another flight from wherever it currently is. This can&rsquo;t be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={busy}>
            Keep flying
          </Button>
          <Button type="button" variant="destructive" onClick={handleConfirm} disabled={busy}>
            {busy ? 'Abandoning…' : 'Abandon flight'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
