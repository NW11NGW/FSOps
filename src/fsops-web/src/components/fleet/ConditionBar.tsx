import { cn } from '@/lib/utils'

interface ConditionBarProps {
  percent: number
  className?: string
}

/** Condition colour bands: healthy above 60, watch 30-60, poor below 30 - not exact thresholds
 *  the backend uses (condition is continuous), just an honest at-a-glance read. */
function bandFor(percent: number): 'success' | 'warning' | 'danger' {
  if (percent >= 60) return 'success'
  if (percent >= 30) return 'warning'
  return 'danger'
}

const FILL: Record<ReturnType<typeof bandFor>, string> = {
  success: 'bg-success',
  warning: 'bg-warning',
  danger: 'bg-danger',
}

const TEXT: Record<ReturnType<typeof bandFor>, string> = {
  success: 'text-success',
  warning: 'text-warning',
  danger: 'text-danger',
}

/** A small horizontal condition gauge - used on the Fleet table and in the buy dialog's used-aircraft preview. */
export function ConditionBar({ percent, className }: ConditionBarProps) {
  const clamped = Math.max(0, Math.min(100, percent))
  const band = bandFor(clamped)

  return (
    <div className={cn('flex items-center gap-2', className)}>
      <div className="h-1.5 w-16 shrink-0 overflow-hidden rounded-full bg-muted">
        <div className={cn('h-full rounded-full transition-[width]', FILL[band])} style={{ width: `${clamped}%` }} />
      </div>
      <span className={cn('text-xs font-medium tabular-nums', TEXT[band])}>{Math.round(clamped)}%</span>
    </div>
  )
}
