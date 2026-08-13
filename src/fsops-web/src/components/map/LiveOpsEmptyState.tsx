import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { PlaneTakeoff, Users, X } from 'lucide-react'

import { Button } from '@/components/ui/button'

const STORAGE_KEY = 'fsops-liveops-empty-collapsed'

/** Wrapped in try/catch, same convention as LiveOpsMap's mapDebugEnabled: `localStorage` can
 *  throw in locked-down embeds (e.g. the in-sim panel webview), and this must never break the
 *  map underneath it. */
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

interface LiveOpsEmptyStateProps {
  /** Whether anything is currently airborne. When true this renders nothing at all - there is
   *  nothing to say and nothing to collapse. */
  hasAircraft: boolean
}

/**
 * The dashboard map's "nothing airborne" message, previously a fixed card that fully covered the
 * bottom of the map with no way to see past it. Dismissible now: closing it collapses to a small
 * pill in the same spot rather than vanishing outright, so the map is immediately usable but the
 * player can still get the message (and the Pilots/Fly shortcuts) back with one click, and Escape
 * closes the full card the same way the close button does.
 *
 * The collapse choice is remembered in localStorage (same pattern as the sidebar's collapsed
 * state and the theme toggle) so it doesn't nag on every visit while nothing is airborne - but it
 * is deliberately re-armed the moment a real flight starts (`hasAircraft` goes true), so the
 * message is waiting again, uncollapsed, the next time there is genuinely nothing to show. A
 * player who dismisses it once is not opting out of ever seeing it again.
 */
export function LiveOpsEmptyState({ hasAircraft }: LiveOpsEmptyStateProps) {
  const [collapsed, setCollapsed] = useState(readCollapsed)

  // A real flight starting re-arms the message: next time the map goes empty again, it should
  // show in full rather than staying collapsed from a dismissal that predates this flight.
  useEffect(() => {
    if (hasAircraft) {
      setCollapsed(false)
      writeCollapsed(false)
    }
  }, [hasAircraft])

  useEffect(() => {
    if (hasAircraft || collapsed) return undefined
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setCollapsed(true)
        writeCollapsed(true)
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => document.removeEventListener('keydown', handleKeyDown)
  }, [hasAircraft, collapsed])

  if (hasAircraft) return null

  if (collapsed) {
    return (
      <div className="pointer-events-none absolute inset-x-3 bottom-3 z-10 flex justify-center">
        <button
          type="button"
          onClick={() => {
            setCollapsed(false)
            writeCollapsed(false)
          }}
          aria-label="Show the live status message: nothing airborne right now"
          className="pointer-events-auto flex items-center gap-1.5 rounded-full border border-border bg-surface-elevated/95 px-3 py-1.5 text-xs text-muted-foreground shadow-elevation-2 transition-colors hover:text-foreground"
        >
          <PlaneTakeoff className="size-3.5 shrink-0" aria-hidden="true" />
          Nothing airborne
        </button>
      </div>
    )
  }

  return (
    <div className="pointer-events-none absolute inset-x-3 bottom-3 z-10 flex justify-center">
      <div className="pointer-events-auto relative flex max-w-md flex-col items-center gap-2 rounded-lg border border-border bg-surface-elevated/95 p-4 pt-8 text-center shadow-elevation-3">
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => {
            setCollapsed(true)
            writeCollapsed(true)
          }}
          aria-label="Hide the live status message"
          className="absolute right-1 top-1 h-7 w-7 p-0 text-muted-foreground hover:text-foreground"
        >
          <X className="size-3.5" aria-hidden="true" />
        </Button>
        <div className="flex size-9 shrink-0 items-center justify-center rounded-full bg-accent/15 text-accent">
          <PlaneTakeoff className="size-4" />
        </div>
        <p className="text-sm font-medium">Nothing airborne right now</p>
        <p className="text-xs text-muted-foreground">
          Hire a pilot and give them a schedule, or fly a route yourself, and it will show up here live.
        </p>
        <div className="flex gap-2 pt-1">
          <Button asChild size="sm" variant="outline">
            <Link to="/pilots">
              <Users className="size-3.5 shrink-0" />
              Pilots
            </Link>
          </Button>
          <Button asChild size="sm">
            <Link to="/fly">
              <PlaneTakeoff className="size-3.5 shrink-0" />
              Fly
            </Link>
          </Button>
        </div>
      </div>
    </div>
  )
}
