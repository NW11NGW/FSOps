import { Banknote, Clock, Coins, ListChecks } from 'lucide-react'

import type { DraftEntry } from './draftEntry'
import { StatTile } from '@/components/shared/StatTile'
import { useSettings } from '@/hooks/useSettings'
import type { PilotSummary } from '@/types/pilot'

interface WeeklySummaryProps {
  draft: DraftEntry[]
  pilot: PilotSummary
  /** True when the on-screen draft differs from what was last saved - the revenue/cost figures
   *  below come from the *saved* schedule, so this caveats them rather than implying they already
   *  reflect edits still pending a save. */
  isDirty: boolean
}

/** A per-pilot weekly readout so the player can judge a schedule before committing to it. Sectors
 *  and hours are computed live from the on-screen draft (always accurate); revenue and cost are
 *  the backend's own projection for the last *saved* schedule, shown only when it has supplied
 *  one - the API contract allows these to be absent in the first cut. */
export function WeeklySummary({ draft, pilot, isDirty }: WeeklySummaryProps) {
  const { fmt } = useSettings()

  const sectors = draft.length
  const hours = draft.reduce((sum, entry) => sum + entry.blockMinutes, 0) / 60

  const hasRevenue = pilot.weeklyEstimatedRevenue != null
  const hasCost = pilot.weeklyEstimatedCost != null

  return (
    <div className="space-y-2">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatTile label="Sectors / week" value={String(sectors)} icon={ListChecks} />
        <StatTile label="Flight hours / week" value={hours.toFixed(1)} icon={Clock} />
        <StatTile
          label="Est. weekly revenue"
          value={hasRevenue ? fmt.money(pilot.weeklyEstimatedRevenue as number) : '—'}
          icon={Banknote}
        />
        <StatTile
          label="Est. weekly cost"
          value={hasCost ? fmt.money(pilot.weeklyEstimatedCost as number) : '—'}
          icon={Coins}
        />
      </div>
      {(!hasRevenue || !hasCost || isDirty) && (
        <p className="text-xs text-muted-foreground">
          {!hasRevenue || !hasCost
            ? 'Revenue and cost estimates are not available yet.'
            : isDirty
              ? 'Revenue and cost reflect the last saved schedule - save to update them for your current edits.'
              : null}
        </p>
      )}
    </div>
  )
}
