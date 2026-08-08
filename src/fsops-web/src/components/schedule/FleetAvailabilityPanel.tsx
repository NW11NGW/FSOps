import { useMemo } from 'react'
import { Plane, ShieldAlert, Wrench } from 'lucide-react'

import type { DraftEntry } from './draftEntry'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import type { FleetAircraftLite } from '@/types/schedule'

interface FleetAvailabilityPanelProps {
  draft: DraftEntry[]
  fleet: FleetAircraftLite[]
}

/** Weekly scheduled hours per aircraft, keyed by fleetAircraftId. */
function weeklyHoursByAircraft(draft: DraftEntry[]): Map<string, number> {
  const totals = new Map<string, number>()
  for (const entry of draft) {
    totals.set(entry.fleetAircraftId, (totals.get(entry.fleetAircraftId) ?? 0) + entry.blockMinutes / 60)
  }
  return totals
}

/**
 * Which aircraft this schedule uses, how many hours a week it flies them, and two things the
 * player needs to see before committing: an aircraft this schedule would fly into its next
 * A-check, and the aircraft being deliberately held back for the player to fly themselves.
 */
export function FleetAvailabilityPanel({ draft, fleet }: FleetAvailabilityPanelProps) {
  const hoursByAircraft = useMemo(() => weeklyHoursByAircraft(draft), [draft])
  const usedIds = new Set(hoursByAircraft.keys())
  const usedAircraft = fleet.filter((a) => usedIds.has(a.id))
  const reservedAircraft = fleet.filter((a) => a.reservedForPlayer)

  if (fleet.length === 0) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Fleet in this schedule</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {usedAircraft.length === 0 && (
          <p className="text-sm text-muted-foreground">No legs scheduled yet - aircraft used will appear here.</p>
        )}

        {usedAircraft.map((aircraft) => {
          const weeklyHours = hoursByAircraft.get(aircraft.id) ?? 0
          const fliesIntoACheck = aircraft.hoursToNextACheck > 0 && weeklyHours >= aircraft.hoursToNextACheck
          return (
            <div key={aircraft.id} className="flex items-start justify-between gap-3 rounded-md border border-border p-2.5 text-sm">
              <span className="flex min-w-0 items-center gap-2">
                <Plane className="size-4 shrink-0 text-accent" aria-hidden="true" />
                <span className="min-w-0 break-words">
                  <span className="font-mono font-medium">{aircraft.registration}</span>
                  <span className="ml-2 text-muted-foreground">{aircraft.family}</span>
                  <p className="text-xs text-muted-foreground">{weeklyHours.toFixed(1)}h scheduled / week</p>
                </span>
              </span>
              {aircraft.groundedReason ? (
                <Badge variant="danger" className="shrink-0">
                  <Wrench className="size-3" /> Grounded
                </Badge>
              ) : fliesIntoACheck ? (
                <Badge variant="warning" className="shrink-0" title={`${aircraft.hoursToNextACheck.toFixed(0)}h to next A-check`}>
                  <Wrench className="size-3" /> Flies into A-check
                </Badge>
              ) : (
                <span className="shrink-0 text-xs text-muted-foreground">
                  {aircraft.hoursToNextACheck.toFixed(0)}h to A-check
                </span>
              )}
            </div>
          )
        })}

        {reservedAircraft.length > 0 && (
          <div className="space-y-1.5 border-t border-border pt-3">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Held back for you</p>
            {reservedAircraft.map((aircraft) => (
              <div key={aircraft.id} className="flex items-center gap-2 text-sm text-muted-foreground">
                <ShieldAlert className="size-4 shrink-0 text-warning" aria-hidden="true" />
                <span className="min-w-0 break-words">
                  <span className="font-mono">{aircraft.registration}</span> is reserved for you to fly and won't be
                  offered to virtual pilots{aircraft.locationIcao ? ` (currently at ${aircraft.locationIcao})` : ''}.
                </span>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
