import { PauseCircle, TriangleAlert, Wrench } from 'lucide-react'

import { Card, CardContent } from '@/components/ui/card'
import { cn } from '@/lib/utils'

interface MaintenanceSuspendToggleProps {
  value: boolean
  onChange: (value: boolean) => void
  disabled?: boolean
}

/**
 * A property of the WHOLE weekly schedule, not any one day or leg - deliberately
 * placed outside the day/leg editing flow, alongside the save toolbar. Mirrors
 * `PilotSchedule.AutoSuspendOnMaintenance`, defaults to true, and must always be sent explicitly
 * on save (see hooks/useSchedule.ts's `save`) - the backend resets an omitted value to true, so a
 * save that forgot to include this would silently switch the player's "off" choice back on.
 */
export function MaintenanceSuspendToggle({ value, onChange, disabled }: MaintenanceSuspendToggleProps) {
  return (
    <Card>
      <CardContent className="space-y-2.5 p-4">
        <div className="flex items-start justify-between gap-3">
          <span className="flex min-w-0 items-start gap-2.5">
            <span className="mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-md bg-accent/15 text-accent">
              <Wrench className="size-4" aria-hidden="true" />
            </span>
            <span className="min-w-0">
              <span className="block text-sm font-medium">Pause during maintenance</span>
              <span className="block text-xs text-muted-foreground">
                {value
                  ? "While this aircraft is grounded for a check, this schedule pauses automatically and picks back up the moment it's released."
                  : 'Flights scheduled during a grounding are cancelled instead of paused.'}
              </span>
            </span>
          </span>
          <button
            type="button"
            role="switch"
            aria-checked={value}
            aria-label="Pause this schedule automatically while its aircraft is in maintenance"
            disabled={disabled}
            onClick={() => onChange(!value)}
            className={cn(
              'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-50',
              value ? 'bg-accent' : 'bg-muted',
            )}
          >
            <span
              className={cn(
                'inline-block size-3.5 transform rounded-full bg-surface shadow transition-transform',
                value ? 'translate-x-[18px]' : 'translate-x-[3px]',
              )}
            />
          </button>
        </div>

        {!value && (
          <p className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-2.5 text-xs text-warning">
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span className="min-w-0 break-words text-foreground/90">
              Every flight this schedule would have flown while the aircraft is grounded gets cancelled instead of
              waiting - under True-life that's a real cancellation fee per flight, so a two-week C-check could mean
              fourteen of them.
            </span>
          </p>
        )}

        {value && (
          <p className="flex items-start gap-2 text-xs text-muted-foreground">
            <PauseCircle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span className="min-w-0 break-words">No cancellation fees for time lost to a check - it's simply flown again once the aircraft is back.</span>
          </p>
        )}
      </CardContent>
    </Card>
  )
}
