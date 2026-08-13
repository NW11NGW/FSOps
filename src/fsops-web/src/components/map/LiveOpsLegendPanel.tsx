import { useState } from 'react'
import { Layers, X } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { AtcMapLegend } from '@/components/map/AtcMapLegend'
import type { VatsimAtcController, VatsimTrafficPilot } from '@/types/operations'

const STORAGE_KEY = 'fsops-liveops-legend-collapsed'

/** Wrapped in try/catch, same convention as LiveOpsMap's mapDebugEnabled and
 *  LiveOpsEmptyState's readCollapsed: `localStorage` can throw in locked-down embeds (e.g. the
 *  in-sim panel webview), and this must never break the map underneath it. */
function readCollapsed(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

function writeCollapsed(value: boolean): void {
  try {
    if (typeof window === 'undefined') return
    if (value) {
      window.localStorage.setItem(STORAGE_KEY, 'true')
    } else {
      window.localStorage.removeItem(STORAGE_KEY)
    }
  } catch {
    // Best-effort only - never let a locked-down storage break the map.
  }
}

interface LiveOpsLegendPanelProps {
  hasAircraft: boolean
  atcControllers: VatsimAtcController[]
  vatsimTraffic: VatsimTrafficPilot[]
}

/**
 * The dashboard map's bottom-right key - what the marker colours/shapes mean (own aircraft vs.
 * virtual, other VATSIM traffic, ATC sector/terminal coverage). Previously a fixed panel with no
 * way to put it away, sitting over the corner of the map for as long as there was anything to key
 * at all. Collapsible now, on the same terms as the "nothing airborne" empty state right next to
 * it: a close control collapses it to a small reopenable pill in the same spot rather than
 * removing it outright, so there is always an obvious way back without a reload.
 *
 * Deliberately consistent with LiveOpsEmptyState's mechanics - same localStorage persistence
 * pattern (a `fsops-*` key, read once on mount, written on every toggle) so the two panels behave
 * the same way for a player who has learned one of them. They part ways on one point: the empty
 * state re-arms itself the moment a flight starts, because "nothing airborne" becoming stale is a
 * meaningful event worth surfacing again unprompted. A collapsed legend has no equivalent - a
 * player who already knows what the colours mean does not need it forced back open just because
 * a new controller logged on - so this simply stays collapsed until the player reopens it
 * themselves.
 */
export function LiveOpsLegendPanel({ hasAircraft, atcControllers, vatsimTraffic }: LiveOpsLegendPanelProps) {
  const [collapsed, setCollapsed] = useState(readCollapsed)

  const hasContent = hasAircraft || atcControllers.length > 0 || vatsimTraffic.length > 0
  if (!hasContent) return null

  const expand = () => {
    setCollapsed(false)
    writeCollapsed(false)
  }
  const collapse = () => {
    setCollapsed(true)
    writeCollapsed(true)
  }

  if (collapsed) {
    return (
      <div className="pointer-events-none absolute bottom-3 right-3 z-10">
        <button
          type="button"
          onClick={expand}
          aria-label="Show the live map legend"
          className="pointer-events-auto flex items-center gap-1.5 rounded-full border border-border bg-surface-elevated/95 px-3 py-1.5 text-xs text-muted-foreground shadow-elevation-2 transition-colors hover:text-foreground"
        >
          <Layers className="size-3.5 shrink-0" aria-hidden="true" />
          Legend
        </button>
      </div>
    )
  }

  return (
    <div className="pointer-events-none absolute bottom-3 right-3 z-10">
      <div className="pointer-events-auto relative flex flex-col gap-1 rounded-md border border-border bg-surface-elevated/90 py-1.5 pl-2.5 pr-7 text-xs text-muted-foreground shadow-elevation-2">
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={collapse}
          aria-label="Hide the live map legend"
          className="absolute right-0.5 top-0.5 h-5 w-5 p-0 text-muted-foreground hover:text-foreground"
        >
          <X className="size-3" aria-hidden="true" />
        </Button>

        {hasAircraft && (
          <div className="flex items-center gap-3">
            <span className="flex items-center gap-1.5">
              <span className="size-2.5 shrink-0 rounded-full bg-accent" />
              You
            </span>
            <span className="flex items-center gap-1.5">
              <span className="size-2.5 shrink-0 rounded-full border-2 border-accent/70 bg-surface-elevated" />
              Virtual
            </span>
          </div>
        )}
        {vatsimTraffic.length > 0 && (
          <span className="flex items-center gap-1.5">
            <span className="size-2 shrink-0 rounded-full bg-muted-foreground/70" />
            Other VATSIM traffic
          </span>
        )}
        {atcControllers.length > 0 && (
          <>
            {hasAircraft && <span className="h-px bg-border" aria-hidden="true" />}
            <span className="flex items-center gap-1.5">
              <span className="size-2.5 shrink-0 rounded-full border-2 border-warning bg-surface-elevated" />
              ATC
            </span>
            <AtcMapLegend controllers={atcControllers} />
          </>
        )}
      </div>
    </div>
  )
}
