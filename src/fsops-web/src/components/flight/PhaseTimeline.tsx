import { Check } from 'lucide-react'

import { useSettings } from '@/hooks/useSettings'
import { formatClockTime, PHASE_LABELS, PHASE_ORDER } from '@/lib/flightFormat'
import { cn } from '@/lib/utils'
import type { FlightPhase } from '@/types/flight'

export interface OoooTimes {
  out: string | null
  off: string | null
  on: string | null
  in: string | null
}

const OOOI_KEY_BY_PHASE: Partial<Record<FlightPhase, keyof OoooTimes>> = {
  TaxiOut: 'out',
  Climb: 'off',
  Landed: 'on',
  Shutdown: 'in',
}

interface PhaseTimelineProps {
  currentPhase: FlightPhase | null
  /** When provided, renders the OOOI clock time captured for the relevant step (report-card mode). */
  ooooTimes?: OoooTimes
  /** Report-card mode: this is a historical/finished flight, so the last phase reached renders as
   *  complete (checkmark) rather than "in progress" (pulsing ring + step number). */
  completed?: boolean
  className?: string
}

/**
 * Horizontal progression through the flight's phases. Steps behind the current phase are marked
 * complete, the current phase is highlighted, everything ahead stays muted. In report-card mode
 * (ooooTimes supplied) the Out/Off/On/In steps also show the clock time they were captured at.
 */
export function PhaseTimeline({ currentPhase, ooooTimes, completed = false, className }: PhaseTimelineProps) {
  const { settings } = useSettings()
  const currentIndex = currentPhase ? PHASE_ORDER.indexOf(currentPhase) : -1

  return (
    <ol className={cn('flex w-full items-start', className)} aria-label="Flight phase progression">
      {PHASE_ORDER.map((phase, index) => {
        const isComplete = currentIndex >= 0 && (index < currentIndex || (completed && index === currentIndex))
        const isCurrent = index === currentIndex && !completed
        const isLast = index === PHASE_ORDER.length - 1
        const ooooKey = OOOI_KEY_BY_PHASE[phase]
        const ooooValue = ooooKey && ooooTimes ? ooooTimes[ooooKey] : null

        return (
          <li key={phase} className={cn('flex flex-1 flex-col items-center', isLast && 'flex-none items-end')}>
            <div className="flex w-full items-center">
              <div
                className={cn(
                  'flex size-6 shrink-0 items-center justify-center rounded-full border-2 text-[10px] font-semibold transition-colors',
                  isComplete && 'border-accent bg-accent text-accent-foreground',
                  isCurrent && 'border-accent bg-background text-accent shadow-[0_0_0_4px_hsl(var(--accent)/0.2)]',
                  !isComplete && !isCurrent && 'border-border bg-muted text-muted-foreground',
                )}
                aria-current={isCurrent ? 'step' : undefined}
              >
                {isComplete ? <Check className="size-3.5" /> : index + 1}
              </div>
              {!isLast && (
                <div className={cn('h-0.5 flex-1 transition-colors', isComplete ? 'bg-accent' : 'bg-border')} />
              )}
            </div>
            <span
              className={cn(
                'mt-1.5 text-center text-[11px] leading-tight',
                isCurrent ? 'font-semibold text-foreground' : isComplete ? 'text-foreground/80' : 'text-muted-foreground',
              )}
            >
              {PHASE_LABELS[phase]}
            </span>
            {ooooKey && (
              <span className="text-[10px] tabular-nums text-muted-foreground">
                {ooooValue ? formatClockTime(ooooValue, settings.timeDisplay === 'Utc', settings.use24HourClock) : '—'}
              </span>
            )}
          </li>
        )
      })}
    </ol>
  )
}
