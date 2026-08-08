import { Check } from 'lucide-react'

import { Skeleton } from '@/components/ui/skeleton'
import type { StrategyProfileMeta } from '@/components/onboarding/wizardData'
import {
  describeAdvisories,
  describeCosts,
  describeFares,
  describeLoadFactor,
  describeSensitivity,
} from '@/lib/strategyProfileCopy'
import { cn } from '@/lib/utils'
import type { StrategyProfileInfo } from '@/types/airline'

interface StrategyProfileCardProps {
  meta: StrategyProfileMeta
  /** undefined while GET /airline/strategy-profiles is still loading - renders skeleton rows. */
  info: StrategyProfileInfo | undefined
  /** True when the figures fetch failed. Without this a failed fetch is indistinguishable from a
   *  slow one and the card shows loading skeletons forever, which reads as "still working" when
   *  nothing is actually in flight. Say so instead - the profile is still selectable. */
  figuresUnavailable?: boolean
  selected: boolean
  onSelect: () => void
}

/**
 * One strategy profile's card: editorial name/tagline (from wizardData.ts) plus the actual
 * fares/sensitivity/load-factor/costs/advisory figures (from useStrategyProfiles, sourced from
 * economy-config.json). Shared between the onboarding wizard and Settings -> Airline so the copy
 * is identical wherever a profile is chosen or changed.
 */
export function StrategyProfileCard({
  meta,
  info,
  figuresUnavailable = false,
  selected,
  onSelect,
}: StrategyProfileCardProps) {
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      className={cn(
        'flex flex-col gap-3 rounded-lg border p-5 text-left transition-colors',
        selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
      )}
    >
      {/* Label and the selected badge share a row - label gets its own wrapping column so it can
       *  never sit under the fixed-size badge, matching StatTile's icon/label layout. */}
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
        <dl className="space-y-1.5 text-xs text-muted-foreground">
          <div>
            <dt className="inline font-medium text-foreground">Fares — </dt>
            <dd className="inline">{describeFares(info)}</dd>
          </div>
          <div>
            <dt className="inline font-medium text-foreground">Price sensitivity — </dt>
            <dd className="inline">{describeSensitivity(info)}</dd>
          </div>
          <div>
            <dt className="inline font-medium text-foreground">Typical load factor — </dt>
            <dd className="inline">{describeLoadFactor(info)}</dd>
          </div>
          <div>
            <dt className="inline font-medium text-foreground">Costs — </dt>
            <dd className="inline">{describeCosts(info)}</dd>
          </div>
          <div>
            <dt className="inline font-medium text-foreground">Route advice — </dt>
            <dd className="inline">{describeAdvisories(info)}</dd>
          </div>
        </dl>
      ) : figuresUnavailable ? (
        <p className="text-xs text-muted-foreground">
          Figures unavailable — you can still choose this profile.
        </p>
      ) : (
        <div className="space-y-1.5">
          <Skeleton className="h-3 w-full" />
          <Skeleton className="h-3 w-5/6" />
          <Skeleton className="h-3 w-4/6" />
          <Skeleton className="h-3 w-full" />
        </div>
      )}
    </button>
  )
}
