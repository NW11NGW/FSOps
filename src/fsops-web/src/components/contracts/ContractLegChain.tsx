import { Check, PlaneTakeoff } from 'lucide-react'

import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { Contract, ContractLeg } from '@/types/contract'

/**
 * A job's progress as one pip per leg - flown, next, still to come.
 *
 * <p>This is the cheapest honest way to show scale. Eleven pips beside one pip says "this is an
 * expedition and that is a hop" faster than any number can, and it is the real leg count rather than
 * a bar scaled to some arbitrary maximum. Capped only in that a very long chain wraps.</p>
 */
export function ContractLegPips({ contract, className }: { contract: Contract; className?: string }) {
  const legs = contract.legs
  if (!legs || legs.length === 0) return null

  const nextSequence = contract.nextLeg?.sequence ?? null

  return (
    <div
      className={cn('flex flex-wrap items-center gap-1', className)}
      role="img"
      aria-label={`${contract.flownLegCount} of ${contract.legCount} legs flown`}
    >
      {legs.map((leg) => (
        <span
          key={leg.id}
          className={cn(
            'h-1.5 w-5 rounded-full transition-colors',
            leg.flown
              ? 'bg-success'
              : leg.sequence === nextSequence
                ? 'bg-accent'
                : 'bg-muted-foreground/30',
          )}
        />
      ))}
    </div>
  )
}

interface LegRowProps {
  leg: ContractLeg
  isNext: boolean
  isLast: boolean
}

function LegRow({ leg, isNext, isLast }: LegRowProps) {
  const { fmt } = useSettings()

  // Flown, but nothing reached the ledger for it. `feePaid` is null only when the leg has not been
  // flown at all, which is a different thing entirely and must not read as "paid nothing".
  const unpaid = leg.flown && (leg.feePaid ?? 0) <= 0

  return (
    <li className="flex gap-3">
      {/* The spine: a marker per stop and a line joining it to the next, so the chain reads as one
       *  journey rather than a list of unrelated sectors. */}
      <div className="flex flex-col items-center">
        <span
          className={cn(
            'flex size-6 shrink-0 items-center justify-center rounded-full border text-[10px] font-semibold tabular-nums',
            leg.flown
              ? 'border-success/40 bg-success/15 text-success'
              : isNext
                ? 'border-accent bg-accent/15 text-accent'
                : 'border-border bg-muted text-muted-foreground',
          )}
        >
          {leg.flown ? <Check className="size-3" /> : leg.sequence}
        </span>
        {!isLast && (
          <span className={cn('w-px flex-1', leg.flown ? 'bg-success/40' : 'bg-border')} aria-hidden="true" />
        )}
      </div>

      <div className={cn('min-w-0 flex-1 pb-4', isLast && 'pb-0')}>
        <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
          <span
            className={cn(
              'font-mono text-sm',
              leg.flown ? 'text-muted-foreground' : isNext ? 'font-semibold text-foreground' : 'text-foreground',
            )}
          >
            {leg.departureIcao} → {leg.arrivalIcao}
          </span>
          {/* A flown leg shows what it PAID, not what it was worth. Those differ whenever a leg was
           *  completed with estimates or invalidated - it counts as flown and pays nothing - and
           *  showing the stamped share there would credit the player with money they never got. */}
          <span
            className={cn(
              'text-xs tabular-nums',
              !leg.flown ? 'text-muted-foreground' : unpaid ? 'text-muted-foreground line-through' : 'text-success',
            )}
          >
            {fmt.money(leg.flown ? (leg.feePaid ?? 0) : leg.feeShare)}
          </span>
        </div>

        <p className="mt-0.5 text-xs text-muted-foreground">
          <span className="tabular-nums">{fmt.distance(leg.distanceNm)}</span>
          <span aria-hidden="true"> · </span>
          <span className="tabular-nums">{fmt.duration(leg.plannedBlockMinutes)}</span>
          {leg.flown && !unpaid && <span className="ml-2 text-success">Flown</span>}
          {/* Quiet and factual. The leg counted and the job moved on; it just did not pay. Saying so
           *  is what stops the totals looking wrong for no visible reason. */}
          {unpaid && (
            <span className="ml-2" title="A leg completed with estimates, or one invalidated by time acceleration or a position jump, counts as flown but pays nothing.">
              Flown · not paid
            </span>
          )}
          {isNext && (
            <span className="ml-2 inline-flex items-center gap-1 font-medium text-accent">
              <PlaneTakeoff className="size-3" />
              Next
            </span>
          )}
        </p>
      </div>
    </li>
  )
}

/**
 * The whole chain of stops, in the order they are flown.
 *
 * <p><b>The order comes from the server and is never re-sorted here.</b> `legs` arrives in sequence
 * order and `nextLeg` names the one that may be started - the two together are exactly why this
 * component does not have to know the rule that a leg cannot begin until the one before it has
 * landed. Working that out independently is how a screen ends up subtly disagreeing with the
 * endpoint that enforces it.</p>
 *
 * <p>For a ferry this is the offer itself. "Bristol to New York" is a fact; eleven stops up the west
 * coast of Greenland is the reason to take the job.</p>
 */
export function ContractLegChain({ contract, className }: { contract: Contract; className?: string }) {
  const legs = contract.legs

  if (!legs || legs.length === 0) {
    return (
      <p className={cn('text-sm text-muted-foreground', className)}>
        This job&rsquo;s legs could not be loaded.
      </p>
    )
  }

  const nextSequence = contract.nextLeg?.sequence ?? null

  return (
    <ol className={cn('space-y-0', className)}>
      {legs.map((leg, index) => (
        <LegRow key={leg.id} leg={leg} isNext={leg.sequence === nextSequence} isLast={index === legs.length - 1} />
      ))}
    </ol>
  )
}
