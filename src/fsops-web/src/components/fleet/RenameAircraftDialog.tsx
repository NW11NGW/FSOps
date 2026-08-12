import { useEffect, useState } from 'react'
import { Shuffle } from 'lucide-react'
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
import { ApiError, get, put } from '@/lib/api'
import type { FleetAircraftSummary } from '@/types/fleet'

interface RenameAircraftDialogProps {
  aircraft: FleetAircraftSummary | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

/**
 * Repaints happen, so an existing aircraft can be renamed from the Fleet page.
 * Same light validation and uniqueness rule as buying/leasing (PUT
 * /fleet/{id}/registration), never a country-format check - a custom entry is deliberately not
 * held to any registry's shape.
 */
export function RenameAircraftDialog({ aircraft, onOpenChange, onSuccess }: RenameAircraftDialogProps) {
  const [registration, setRegistration] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [suggesting, setSuggesting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Depend on the primitive id, NOT `aircraft` itself - Fleet.tsx constructs `aircraft` as a
  // fresh object literal on every render it makes (e.g. from a live cash-balance heartbeat
  // elsewhere in the tree), so a reference-identity dependency here would reset the registration
  // field on every unrelated re-render - wiping out whatever the player had just typed. Same fix
  // as EndLeaseDialog's `target?.id` / SellAircraftDialog's `aircraft?.id` dependency.
  useEffect(() => {
    setRegistration(aircraft?.registration ?? '')
    setError(null)
  }, [aircraft?.id])

  async function handleRandomise() {
    setSuggesting(true)
    try {
      const result = await get<{ registration: string }>('/fleet/registration-suggestion')
      setRegistration(result.registration)
    } catch {
      // The current value is left alone - randomising is a nicety, not required to close the dialog.
    } finally {
      setSuggesting(false)
    }
  }

  async function handleSubmit() {
    if (!aircraft) return
    setSubmitting(true)
    setError(null)

    try {
      await put(`/fleet/${aircraft.id}/registration`, { registration })
      toast.success(`Renamed to ${registration}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not rename this aircraft. Check your connection and try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={aircraft !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle>Rename aircraft</DialogTitle>
          <DialogDescription>{aircraft?.aircraftTypeName} &middot; currently {aircraft?.registration}</DialogDescription>
        </DialogHeader>

        <div className="space-y-1.5">
          <label htmlFor="rename-registration" className="text-sm font-medium">
            Registration
          </label>
          <div className="flex gap-2">
            <Input
              id="rename-registration"
              value={registration}
              onChange={(e) => setRegistration(e.target.value.toUpperCase())}
              placeholder="e.g. G-EZBA"
              maxLength={10}
              className="font-mono uppercase"
              disabled={suggesting}
            />
            <Button type="button" variant="outline" size="icon" onClick={handleRandomise} disabled={suggesting} aria-label="Randomise registration">
              <Shuffle />
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">Letters, digits and hyphens only, unique within your fleet.</p>
        </div>

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={!registration.trim() || submitting}>
            {submitting ? 'Saving…' : 'Save'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
