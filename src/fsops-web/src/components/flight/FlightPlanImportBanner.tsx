import { CloudDownload, Info } from 'lucide-react'

import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import type { FlightPlanImportStatus } from '@/hooks/useFlightPlanImport'
import type { FlightPlanImport } from '@/types/flight'

interface FlightPlanImportBannerProps {
  planImport: FlightPlanImport | null
  status: FlightPlanImportStatus
}

/**
 * Tells the player, before they commit to "Start flight", whether this sector's plan is coming
 * from their SimBrief OFP or FSOps' own estimate, and if it fell back, why - said plainly rather
 * than substituted silently. Only shown once a route/aircraft is picked and a SimBrief Pilot ID is configured;
 * players who never set one up see nothing here, matching the "no lock-in" rule that SimBrief is
 * a convenience, never a requirement.
 */
export function FlightPlanImportBanner({ planImport, status }: FlightPlanImportBannerProps) {
  const { settings, fmt } = useSettings()

  if (!settings.simBriefPilotId) {
    return null
  }

  if (status === 'idle') {
    return null
  }

  if (status === 'loading' && !planImport) {
    return <Skeleton className="h-14 w-full" />
  }

  if (status === 'error' || !planImport || !planImport.available) {
    return null
  }

  if (planImport.fromSimBrief) {
    return (
      <div className="flex items-start gap-2 rounded-md border border-accent/30 bg-accent/5 p-3 text-xs text-muted-foreground">
        <CloudDownload className="mt-0.5 size-3.5 shrink-0 text-accent" aria-hidden="true" />
        <p className="min-w-0 break-words">
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
        </p>
      </div>
    )
  }

  return (
    <div className="flex items-start gap-2 rounded-md border border-border bg-muted/20 p-3 text-xs text-muted-foreground">
      <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      <p className="min-w-0 break-words">
        <span className="font-medium text-foreground">Using the built-in plan.</span>{' '}
        {planImport.message ?? "SimBrief didn't have a usable plan for this sector."}
      </p>
    </div>
  )
}
