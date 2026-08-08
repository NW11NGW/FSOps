import { Check } from 'lucide-react'

import type { PlaystyleMeta } from '@/components/onboarding/wizardData'
import { Skeleton } from '@/components/ui/skeleton'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { CurrencyInfo } from '@/types/settings'
import type { PlaystyleInfo } from '@/types/airline'

interface PlaystyleCardProps {
  meta: PlaystyleMeta
  /** undefined while GET /airline/playstyles is still loading - renders skeleton rows. */
  info: PlaystyleInfo | undefined
  figuresUnavailable?: boolean
  currency: CurrencyInfo
  selected: boolean
  /** Omit for a read-only card (Settings -> Airline, where the choice is permanent) - renders a
   *  non-interactive block instead of a clickable button. */
  onSelect?: () => void
}

/**
 * One playstyle's card: editorial name/tagline (above) plus the actual starting-capital/lease
 * deposit/starter-lease/insurance figures (from usePlaystyles, sourced from economy-config.json).
 * Shared between the onboarding wizard and the read-only Settings -> Airline display, so the copy
 * is identical wherever a playstyle is shown.
 */
export function PlaystyleCard({ meta, info, figuresUnavailable = false, currency, selected, onSelect }: PlaystyleCardProps) {
  const className = cn(
    'flex flex-col gap-3 rounded-lg border p-5 text-left transition-colors',
    selected ? 'border-accent bg-accent/10' : 'border-border bg-surface',
    onSelect && !selected && 'hover:border-accent/40',
  )

  const body = (
    <>
      <div className="flex items-start justify-between gap-3">
        <span className="min-w-0 break-words text-base font-semibold tracking-tight">{meta.label}</span>
        {selected && (
          <span className="flex size-5 shrink-0 items-center justify-center rounded-full bg-accent text-accent-foreground">
            <Check className="size-3" />
          </span>
        )}
      </div>
      <p className="text-xs font-medium uppercase tracking-wide text-accent">{meta.tagline}</p>

      {info ? (
        <>
          <p className="text-xs text-muted-foreground">{info.description}</p>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs text-muted-foreground">
            <div>
              <dt className="text-foreground">Starting capital</dt>
              <dd className="font-medium tabular-nums">{formatMoney(info.startingCapital, currency)}</dd>
            </div>
            <div>
              <dt className="text-foreground">Lease deposit</dt>
              <dd className="font-medium tabular-nums">{info.leaseDepositMonths} month(s)</dd>
            </div>
            <div>
              <dt className="text-foreground">Starter lease (A320)</dt>
              <dd className="font-medium tabular-nums">{formatMoney(info.starterLeaseRateA320, currency)}/mo</dd>
            </div>
            <div>
              <dt className="text-foreground">Monthly insurance</dt>
              <dd className="font-medium tabular-nums">{formatMoney(info.monthlyInsurancePerAircraft, currency)}</dd>
            </div>
          </dl>
        </>
      ) : figuresUnavailable ? (
        <p className="text-xs text-muted-foreground">Figures unavailable.</p>
      ) : (
        <div className="space-y-1.5">
          <Skeleton className="h-3 w-full" />
          <Skeleton className="h-3 w-5/6" />
          <Skeleton className="h-3 w-4/6" />
        </div>
      )}
    </>
  )

  if (!onSelect) {
    return <div className={className}>{body}</div>
  }

  return (
    <button type="button" onClick={onSelect} aria-pressed={selected} className={className}>
      {body}
    </button>
  )
}
