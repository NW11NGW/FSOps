import { CloudDownload, Info, RefreshCw } from 'lucide-react'
import { Link } from 'react-router-dom'

import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import type { FlightPlanImportStatus } from '@/hooks/useFlightPlanImport'
import { cn } from '@/lib/utils'
import type { FlightPlanImport } from '@/types/flight'

interface FlightPlanImportBannerProps {
  planImport: FlightPlanImport | null
  status: FlightPlanImportStatus
  /** Re-checks SimBrief right now for this route/aircraft, bypassing the auto-fetch's debounce -
   *  see useFlightPlanImport.refresh for why this needs to exist as its own action rather than
   *  only firing on selection change. */
  onRefresh: () => void
}

/**
 * Tells the player, before they commit to "Start flight", whether this sector's plan is coming
 * from their SimBrief OFP or FSOps' own estimate, and if it fell back, why - said plainly rather
 * than substituted silently. Fetches automatically once a route/aircraft is picked, and again on
 * demand via the "Check for OFP" button - added after the first real flight showed the automatic
 * fetch alone isn't enough: a player who plans in SimBrief in another tab after the Fly screen has
 * already settled on a selection has no way to make it look again without reselecting.
 * SimBrief is a convenience, never a requirement: a player who never sets a Pilot ID is told
 * plainly where to add one rather than seeing nothing at all, and every failure mode here (no
 * Pilot ID, no plan on file, SimBrief unreachable, a plan for the wrong city pair) fails soft into
 * the same "using the built-in plan" state - never a blocked or broken Fly screen.
 */
export function FlightPlanImportBanner({ planImport, status, onRefresh }: FlightPlanImportBannerProps) {
  const { settings, status: settingsStatus, fmt } = useSettings()
  const loading = status === 'loading'

  if (settingsStatus === 'loading') {
    return <Skeleton className="h-14 w-full" />
  }

  if (!settings.simBriefPilotId) {
    return (
      <div className="flex items-start gap-2 rounded-md border border-border bg-muted/20 p-3 text-xs text-muted-foreground">
        <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
        <span className="min-w-0 break-words">
          Add your SimBrief Pilot ID in{' '}
          <Link to="/settings" className="text-accent underline-offset-2 hover:underline">
            Settings
          </Link>{' '}
          to fetch your OFP here — entirely optional.
        </span>
      </div>
    )
  }

  return (
    <div className="space-y-2 rounded-md border border-border p-3">
      <div className="flex items-center justify-between gap-2">
        <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          <CloudDownload className="size-3.5" aria-hidden="true" />
          SimBrief OFP
        </p>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={onRefresh}
          disabled={loading}
          className="h-7 gap-1.5 px-2 text-xs"
        >
          <RefreshCw className={cn('size-3.5', loading && 'animate-spin')} aria-hidden="true" />
          {loading ? 'Checking…' : 'Check for OFP'}
        </Button>
      </div>

      {/* Only the very first check (nothing fetched yet at all) shows a skeleton - a refresh of an
       *  already-loaded plan keeps that plan on screen (below) with the button alone showing the
       *  loading state, rather than yanking the player's last known-good plan off screen while it
       *  re-checks. */}
      {loading && !planImport && <Skeleton className="h-10 w-full" />}

      {status === 'error' && (
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>Could not reach FSOps to check for a plan. Check your connection and try again.</span>
        </p>
      )}

      {status === 'idle' && !loading && (
        <p className="text-xs text-muted-foreground">Not checked yet.</p>
      )}

      {planImport && !planImport.available && (
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>{planImport.message ?? 'Nothing to plan against for this sector yet.'}</span>
        </p>
      )}

      {planImport?.available && planImport.fromSimBrief && (
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <CloudDownload className="mt-0.5 size-3.5 shrink-0 text-accent" aria-hidden="true" />
          <span className="min-w-0 break-words">
            <span className="font-medium text-foreground">Plan imported from SimBrief.</span>{' '}
            {planImport.cruiseAltitudeFt !== null && <>{fmt.altitude(planImport.cruiseAltitudeFt)} cruise, </>}
            {planImport.blockTimeMinutes !== null && <>{fmt.duration(planImport.blockTimeMinutes)} block time, </>}
            {planImport.blockFuelKg !== null && <>{fmt.weight(planImport.blockFuelKg)} block fuel</>}
            {planImport.routeString && (
              <>
                {' '}
                — <span className="font-mono">{planImport.routeString}</span>
              </>
            )}
            .
          </span>
        </p>
      )}

      {planImport?.available && !planImport.fromSimBrief && (
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span className="min-w-0 break-words">
            <span className="font-medium text-foreground">Using the built-in plan.</span>{' '}
            {planImport.message ?? "SimBrief didn't have a usable plan for this sector."}
          </span>
        </p>
      )}
    </div>
  )
}
