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

        {/* min-w-0 below is load-bearing, not tidying. That div is a grid item of DialogContent, so
          *  its min-width resolves to `auto` - meaning min-content - and the destination rows use
          *  `truncate`, which sets white-space: nowrap. That makes their min-content width the FULL
          *  untruncated airport name ("Charles de Gaulle International Airport - Paris
          *  (Roissy-en-France, Val-d'Oise)"), so this column refused to shrink below it and every
          *  panel inside the dialog hung off the right-hand edge. Measured at a 639px window: 446px
          *  of dialog holding 563px of content. Truncation cannot rescue a box that is not allowed
          *  to be narrow in the first place. */}
        {status === 'ready' && options && options.canReposition && (
          <div className="min-w-0 space-y-3">
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

            {/* And min-w-0 again here for a second reason: a <fieldset> defaults to
              *  min-width: min-content in every browser, which no amount of shrinking above it
              *  overrides. Both this and the grid item above have to be freed - zeroing either one
              *  alone changed nothing, which is what made this look like a mystery rather than a
              *  one-line CSS default. */}
            <fieldset className="min-w-0 space-y-1.5">
              <legend className="pb-1.5 text-sm font-medium">Move it to</legend>
              {/* The box shows WHOLE ROWS ONLY. `max-h-56` was an arbitrary height that happened to
                *  land 86% of the way down a row, so the last destination was sliced through its own
                *  text - which reads as a broken box, and was reported as one twice.
                *
                *  Two earlier attempts are worth not repeating. Making the box taller only moved the
                *  cut (one more destination than it was sized for puts it straight back) and pushed a
                *  673px dialog onto a 720px window. A fade over the cut was worse in a subtler way: it
                *  restyled the symptom so the slice looked deliberate, while the box still could not
                *  fit its own data. The reporter was unmoved, correctly.
                *
                *  The height is now DERIVED from the row height rather than guessed, so the two cannot
                *  drift apart: --row-h is the single source of truth, rows are exactly that tall, and
                *  the box is four of them plus the three 1.5-unit gaps between. Scroll snapping keeps
                *  that true after scrolling as well - without it the box starts honest and cuts a row
                *  the moment you move it. */}
              <div
                className="[--row-gap:0.375rem] [--row-h:3.25rem] max-h-[calc(4*var(--row-h)+3*var(--row-gap))] snap-y snap-mandatory space-y-1.5 overflow-y-auto pr-2 [scrollbar-gutter:stable]"
              >
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
                        // h-[var(--row-h)] and snap-start are what make the box's derived height true;
                        // see the container above. Without the fixed height a longer airport name
                        // would grow one row and put the cut back.
                        'flex h-[var(--row-h)] w-full snap-start items-center justify-between gap-3 rounded-md border px-3 py-2 text-left text-sm transition-colors',
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
