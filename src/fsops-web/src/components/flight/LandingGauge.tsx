import { assessLanding } from '@/lib/flightFormat'
import { cn } from '@/lib/utils'

interface LandingGaugeProps {
  fpm: number
}

const SCALE_MAX_MAGNITUDE = 600

const VERDICT_COLOR: Record<ReturnType<typeof assessLanding>['verdict'], string> = {
  smooth: 'text-success',
  firm: 'text-warning',
  hard: 'text-danger',
}

const VERDICT_FILL: Record<ReturnType<typeof assessLanding>['verdict'], string> = {
  smooth: 'bg-success',
  firm: 'bg-warning',
  hard: 'bg-danger',
}

/** The report card's headline: touchdown rate on a visual scale with an honest, non-judgemental verdict. */
export function LandingGauge({ fpm }: LandingGaugeProps) {
  const quality = assessLanding(fpm)
  const fillPercent = Math.min(100, (quality.magnitude / SCALE_MAX_MAGNITUDE) * 100)

  return (
    <div className="space-y-3">
      <div className="flex items-end justify-between gap-4">
        <div>
          <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Touchdown rate</p>
          <p className="font-mono text-4xl font-semibold tabular-nums tracking-tight">
            {Math.round(quality.displayFpm)} <span className="text-lg font-medium text-muted-foreground">fpm</span>
          </p>
        </div>
        <p className={cn('text-lg font-semibold', VERDICT_COLOR[quality.verdict])}>{quality.label}</p>
      </div>

      <div className="relative h-2.5 w-full overflow-hidden rounded-full bg-muted" role="img" aria-label={`Touchdown rate ${Math.round(quality.displayFpm)} feet per minute — ${quality.label.toLowerCase()}`}>
        {/* Reference bands: smooth (0-200), firm (200-400), hard (400+) against the same 0-600 scale as the fill. */}
        <div className="absolute inset-y-0 left-0 bg-success/25" style={{ width: `${(200 / SCALE_MAX_MAGNITUDE) * 100}%` }} />
        <div
          className="absolute inset-y-0 bg-warning/25"
          style={{ left: `${(200 / SCALE_MAX_MAGNITUDE) * 100}%`, width: `${(200 / SCALE_MAX_MAGNITUDE) * 100}%` }}
        />
        <div
          className="absolute inset-y-0 bg-danger/20"
          style={{ left: `${(400 / SCALE_MAX_MAGNITUDE) * 100}%`, right: 0 }}
        />
        <div
          className={cn('absolute inset-y-0 left-0 rounded-full transition-[width] duration-500', VERDICT_FILL[quality.verdict])}
          style={{ width: `${fillPercent}%` }}
        />
      </div>
      <div className="flex justify-between text-[10px] text-muted-foreground">
        <span>0</span>
        <span>−200</span>
        <span>−400</span>
        <span>−600+ fpm</span>
      </div>
    </div>
  )
}
