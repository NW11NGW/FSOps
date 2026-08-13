import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, ArrowRight, Ban, MapPin, Plane, RefreshCw } from 'lucide-react'
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
import { Skeleton } from '@/components/ui/skeleton'
import { useRepositionOptions } from '@/hooks/useRepositionOptions'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { FleetAircraftSummary, RepositionResult } from '@/types/fleet'

interface RepositionAircraftDialogProps {
  aircraft: FleetAircraftSummary | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

/**
 * Moving an idle aircraft to another airport the airline already serves, without flying it there -
 * the fix for an airframe stranded somewhere with nothing useful to do.
 *
 * Two things this dialog is deliberately careful about, because it spends the player's money:
 *
 * 1. **It never fires on a single click.** Picking a destination is one step; confirming the spend
 *    is a separate, explicitly-labelled one that names the aircraft, both airports, the fee and the
 *    resulting cash balance. No destination is pre-selected, so there is no "confirm" to hit until
 *    the player has actively chosen where the aircraft is going.
 * 2. **Every figure comes from the server.** The fee and the resulting balance are read from
 *    `reposition-options`, never re-derived here, so what the confirmation promises cannot disagree
 *    with what the commit posts. The confirmed fee travels back with the commit
 *    (`expectedCost`); if it moved in between - a config reload is enough - the server refuses and
 *    this re-quotes rather than silently charging a different number, exactly as the sell and
 *    end-lease dialogs do.
 *
 * `blockReason` is rendered verbatim: it is server-authored specifically so that every refusal ends
 * in an action that actually works, and paraphrasing it here would let that drift.
 */
export function RepositionAircraftDialog({ aircraft, onOpenChange, onSuccess }: RepositionAircraftDialogProps) {
  const { status, options, refetch } = useRepositionOptions(aircraft?.id ?? null)
  const { fmt } = useSettings()

  const [selectedIcao, setSelectedIcao] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [costChangedFrom, setCostChangedFrom] = useState<number | null>(null)
  const awaitingRevalidation = useRef(false)
  const submittedCost = useRef<number | null>(null)

  // Depend on the primitive id, NOT `aircraft` itself - Fleet.tsx constructs it as a fresh object
  // literal on every render it makes (e.g. from a live cash-balance heartbeat elsewhere in the
  // tree), so a reference-identity dependency would clear the player's chosen destination on every
  // unrelated re-render. Same fix as SellAircraftDialog's and EndLeaseDialog's id dependencies.
  useEffect(() => {
    setSelectedIcao(null)
    setError(null)
    setCostChangedFrom(null)
    awaitingRevalidation.current = false
  }, [aircraft?.id])

  // Once a re-fetch triggered by a refused move resolves, decide whether the fee actually moved
  // (show a re-quote banner) or the move is simply blocked now for another reason - the block-reason
  // banner below already covers that from `options.canReposition`.
  useEffect(() => {
    if (!awaitingRevalidation.current || status !== 'ready') return
    awaitingRevalidation.current = false
    if (options && options.canReposition && submittedCost.current !== null && options.cost !== submittedCost.current) {
      setCostChangedFrom(submittedCost.current)
      setError(null)
    }
  }, [status, options])

  const selected = options?.destinations.find((d) => d.icao === selectedIcao) ?? null
  const canSubmit = status === 'ready' && options !== null && options.canReposition && selected !== null && !submitting

  async function handleSubmit() {
    if (!aircraft || !options || !selectedIcao) return
    setSubmitting(true)
    setError(null)
    setCostChangedFrom(null)
    submittedCost.current = options.cost
    try {
      const result = await post<RepositionResult>(`/fleet/${aircraft.id}/reposition`, {
        destinationIcao: selectedIcao,
        expectedCost: options.cost,
      })
      toast.success(
        `${result.registration} moved to ${result.toIcao} for ${fmt.money(result.cost)}. Cash balance now ${fmt.money(result.cashBalance)}.`,
      )
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      // A refusal here can legitimately mean "the fee moved since you looked" rather than a hard
      // failure - re-quote instead of just reporting the error text.
      setError(err instanceof ApiError ? err.message : 'Could not reposition this aircraft. Check your connection and try again.')
      awaitingRevalidation.current = true
      refetch()
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={aircraft !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Move {aircraft?.registration}?</DialogTitle>
          <DialogDescription>
            Repositions the aircraft to another airport you fly to or from, without flying it there. It arrives
            immediately &mdash; no block time, no airframe hours.
          </DialogDescription>
        </DialogHeader>

        {status === 'loading' && (
          <div className="space-y-2">
            <Skeleton className="h-20 w-full" />
            <Skeleton className="h-24 w-full" />
          </div>
        )}

        {status === 'error' && (
          <p className="text-sm text-danger">Could not load repositioning options. Check your connection and try again.</p>
        )}

        {status === 'ready' && options && !options.canReposition && (
          <div className="flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 p-3 text-sm text-danger">
            <Ban className="mt-0.5 size-4 shrink-0" />
            <span>{options.blockReason}</span>
          </div>
        )}

        {status === 'ready' && options && options.canReposition && (
          <div className="space-y-3">
            {costChangedFrom !== null && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <RefreshCw className="mt-0.5 size-4 shrink-0" />
                <span>
                  The repositioning fee changed since you opened this dialog &mdash; it was {fmt.money(costChangedFrom)}, it's now{' '}
                  {fmt.money(options.cost)}. Review below and confirm again.
                </span>
              </div>
            )}

            <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
              <Plane className="size-4 shrink-0 text-accent" />
              <span className="min-w-0">
                <span className="font-medium">{options.registration}</span>{' '}
                <span className="text-muted-foreground">is at</span>{' '}
                <span className="font-mono font-medium">{options.currentIcao}</span>
                {options.currentAirportName && <span className="text-muted-foreground"> &middot; {options.currentAirportName}</span>}
              </span>
            </div>

            <fieldset className="space-y-1.5">
              <legend className="pb-1.5 text-sm font-medium">Move it to</legend>
              <div className="max-h-56 space-y-1.5 overflow-y-auto pr-1">
                {options.destinations.map((destination) => {
                  const isSelected = destination.icao === selectedIcao
                  return (
                    <button
                      key={destination.icao}
                      type="button"
                      role="radio"
                      aria-checked={isSelected}
                      onClick={() => setSelectedIcao(destination.icao)}
                      className={cn(
                        'flex w-full items-center justify-between gap-3 rounded-md border px-3 py-2 text-left text-sm transition-colors',
                        isSelected
                          ? 'border-accent bg-accent/10 text-foreground'
                          : 'border-border hover:bg-muted',
                      )}
                    >
                      <span className="flex min-w-0 items-center gap-2">
                        <MapPin className={cn('size-4 shrink-0', isSelected ? 'text-accent' : 'text-muted-foreground')} />
                        <span className="min-w-0">
                          <span className="font-mono font-medium">{destination.icao}</span>
                          <span className="block truncate text-xs text-muted-foreground">
                            {destination.municipality ? `${destination.name} · ${destination.municipality}` : destination.name}
                          </span>
                        </span>
                      </span>
                      <span className="shrink-0 text-xs text-muted-foreground">
                        {destination.routeCount === 1 ? '1 route' : `${destination.routeCount} routes`}
                      </span>
                    </button>
                  )
                })}
              </div>
            </fieldset>

            <div className="space-y-2 rounded-md border border-border p-3 text-sm">
              <div className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground">Move</span>
                <span className="flex items-center gap-1.5 font-mono font-medium">
                  {options.currentIcao}
                  <ArrowRight className="size-3.5 text-muted-foreground" />
                  {selected ? selected.icao : <span className="text-muted-foreground">select an airport</span>}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Cost</span>
                <span className="font-semibold tabular-nums text-danger">{fmt.money(options.cost)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Cash balance now</span>
                <span className="tabular-nums">{fmt.money(options.cashBalance)}</span>
              </div>
              <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                <span className="font-medium">Cash balance after</span>
                <span className="font-semibold tabular-nums">{fmt.money(options.cashAfter)}</span>
              </div>
            </div>

            {aircraft?.reservedForPlayer === false && (
              <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" />
                <span>This aircraft is available to virtual pilots &mdash; only aircraft reserved for you can be moved.</span>
              </div>
            )}
          </div>
        )}

        {error && costChangedFrom === null && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          {status === 'ready' && options?.canReposition && (
            <Button onClick={handleSubmit} disabled={!canSubmit}>
              {submitting
                ? 'Moving…'
                : selected
                  ? `Move to ${selected.icao} for ${fmt.money(options.cost)}`
                  : 'Select an airport'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
