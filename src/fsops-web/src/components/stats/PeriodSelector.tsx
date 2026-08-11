import { cn } from '@/lib/utils'

interface PeriodSelectorProps {
  value: number
  onChange: (days: number) => void
}

const OPTIONS = [7, 30, 90] as const

/** A trailing real-day window, never a calendar month - the same "state the period on screen" rule
 *  the Finances page follows, since billing runs on a 30-day rolling cycle from the airline's own
 *  clock rather than the 1st of any month. */
export function PeriodSelector({ value, onChange }: PeriodSelectorProps) {
  return (
    <div className="inline-flex items-center gap-1 rounded-md border border-border bg-surface p-1" role="group" aria-label="Trailing period">
      {OPTIONS.map((days) => (
        <button
          key={days}
          type="button"
          onClick={() => onChange(days)}
          aria-pressed={value === days}
          className={cn(
            'rounded-sm px-3 py-1 text-xs font-medium transition-colors',
            value === days ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {days}d
        </button>
      ))}
    </div>
  )
}
