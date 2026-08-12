import { Link } from 'react-router-dom'
import { CheckCircle2, ChevronRight, Plane, TriangleAlert } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import type { AircraftOption } from '@/types/schedule'

interface AircraftPickerProps {
  options: AircraftOption[]
  status: 'loading' | 'ready' | 'error'
  selectedAircraftId?: string | null
  onSelect: (option: AircraftOption) => void
  /** Closes the host dialog before a "Fleet page" fix-it link navigates away. */
  onNavigateAway: () => void
}

/** The backend's reason already ends in the fix - one short reason ending in an action the player
 *  can take. This only decides whether that fix is a Fleet-page
 *  link worth turning into an actual `<Link>` rather than plain text - a grounded aircraft's
 *  reason has no such action (nothing to click, only time fixes it). */
function fleetPageFix(reason: string | null): boolean {
  return reason !== null && reason.toLowerCase().includes('fleet page')
}

/**
 * Step one of the redesigned two-step picker: pick the aircraft first, then the leg. The original
 * route x aircraft cross-product could not be scanned - six near-identical paragraphs for one slot.
 * Every aircraft is always listed - reserved or grounded ones too, disabled with the backend's own
 * one-line reason - never hidden, since silent omission is exactly the bug this design replaces.
 */
export function AircraftPicker({ options, status, selectedAircraftId, onSelect, onNavigateAway }: AircraftPickerProps) {
  if (status === 'loading') {
    return (
      <div className="space-y-2">
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
      </div>
    )
  }

  if (status === 'error') {
    return <p className="text-sm text-danger">Could not load aircraft for this day. Try again.</p>
  }

  if (options.length === 0) {
    return (
      <p className="rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground">
        No aircraft in your fleet yet - buy or lease one on the Fleet page first.
      </p>
    )
  }

  return (
    <div className="space-y-1.5">
      {options.map((option) => {
        const selected = option.fleetAircraftId === selectedAircraftId
        const showFleetLink = !option.eligible && fleetPageFix(option.reason)
        return (
          <div
            key={option.fleetAircraftId}
            className={cn(
              'rounded-md border p-2.5 text-sm transition-colors',
              !option.eligible && 'bg-muted/20 opacity-90',
              selected ? 'border-accent bg-accent/10' : 'border-border',
            )}
          >
            <button
              type="button"
              disabled={!option.eligible}
              onClick={() => onSelect(option)}
              className={cn(
                'flex w-full items-start gap-2.5 text-left',
                option.eligible && 'cursor-pointer',
                !option.eligible && 'cursor-not-allowed',
              )}
            >
              <Plane className={cn('mt-0.5 size-4 shrink-0', option.eligible ? 'text-accent' : 'text-muted-foreground')} aria-hidden="true" />
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-2">
                  <span className="font-mono font-medium">{option.registration}</span>
                  <span className="text-xs text-muted-foreground">{option.aircraftTypeName}</span>
                  {option.scheduledLegsThisWeek > 0 && (
                    <Badge variant="muted" className="shrink-0">
                      {option.scheduledLegsThisWeek} leg{option.scheduledLegsThisWeek === 1 ? '' : 's'} scheduled
                    </Badge>
                  )}
                </span>
                <span className="mt-0.5 flex items-start gap-1.5 text-xs text-muted-foreground">
                  {option.eligible ? (
                    <>
                      <CheckCircle2 className="mt-0.5 size-3.5 shrink-0 text-success" aria-hidden="true" />
                      <span className="min-w-0 break-words">Currently at {option.locationIcao}</span>
                    </>
                  ) : (
                    <>
                      <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden="true" />
                      <span className="min-w-0 break-words">{option.reason}</span>
                    </>
                  )}
                </span>
              </span>
              {option.eligible && <ChevronRight className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />}
            </button>
            {showFleetLink && (
              <Link
                to="/fleet"
                onClick={onNavigateAway}
                className="ml-6 mt-1 inline-flex items-center text-xs font-medium text-accent underline-offset-2 hover:underline"
              >
                Release it on the Fleet page
              </Link>
            )}
          </div>
        )
      })}
    </div>
  )
}
