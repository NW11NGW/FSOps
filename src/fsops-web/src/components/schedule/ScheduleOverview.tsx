import { useMemo, useState } from 'react'
import { LayoutGrid, Plane, Users } from 'lucide-react'

import { pilotSwatch } from './pilotColor'
import { EmptyState } from '@/components/shared/EmptyState'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useScheduleOverview } from '@/hooks/useSchedule'
import { cn } from '@/lib/utils'
import { DAY_DISPLAY_ORDER, DAY_SHORT_LABELS, timeToMinutes } from '@/types/schedule'
import type { OverviewLeg, OverviewPilotRow } from '@/types/schedule'

function legsByDay(legs: OverviewLeg[]) {
  const byDay = new Map<number, OverviewLeg[]>()
  for (const leg of legs) {
    const list = byDay.get(leg.dayOfWeek) ?? []
    list.push(leg)
    byDay.set(leg.dayOfWeek, list)
  }
  for (const list of byDay.values()) {
    list.sort((a, b) => timeToMinutes(a.departureTimeUtc) - timeToMinutes(b.departureTimeUtc))
  }
  return byDay
}

/** One coloured, clickable-nowhere chip for a leg - the overview is read-only (docs/PLAN.md "2a":
 *  "editing stays in the per-pilot and per-aircraft views"). */
function LegChip({ leg, colourClass, subtitle }: { leg: OverviewLeg; colourClass: string; subtitle: string }) {
  return (
    <div
      className={cn('w-full rounded border px-1.5 py-1 text-left text-[11px] leading-tight', colourClass)}
      title={`${leg.departureIcao ?? '?'} → ${leg.arrivalIcao ?? '?'} at ${leg.departureTimeUtc.slice(0, 5)} UTC · ${subtitle}`}
    >
      <p className="truncate font-mono font-medium">{leg.flightNumber ?? `${leg.departureIcao}${leg.arrivalIcao}`}</p>
      <p className="truncate opacity-80">{leg.departureTimeUtc.slice(0, 5)} UTC</p>
    </div>
  )
}

function WeekRow({
  label,
  sublabel,
  legs,
  colourClass,
}: {
  label: string
  sublabel: string
  legs: OverviewLeg[]
  colourClass: (leg: OverviewLeg) => string
}) {
  const byDay = useMemo(() => legsByDay(legs), [legs])

  return (
    <div className="grid grid-cols-[minmax(120px,160px)_repeat(7,minmax(90px,1fr))] gap-2 border-b border-border py-2 last:border-b-0">
      <div className="flex min-w-0 flex-col justify-center gap-0.5 pr-2">
        <span className="min-w-0 truncate font-mono text-sm font-semibold">{label}</span>
        <span className="min-w-0 truncate text-xs text-muted-foreground">{sublabel}</span>
      </div>
      {DAY_DISPLAY_ORDER.map((day) => {
        const dayLegs = byDay.get(day) ?? []
        return (
          <div key={day} className="min-w-0 space-y-1">
            {dayLegs.length === 0 ? (
              <div className="flex h-full min-h-[2.25rem] items-center justify-center rounded border border-dashed border-border/60 text-[10px] text-muted-foreground">
                —
              </div>
            ) : (
              dayLegs.map((leg) => (
                <LegChip
                  key={`${leg.routeId}-${leg.departureTimeUtc}-${leg.dayOfWeek}`}
                  leg={leg}
                  colourClass={colourClass(leg)}
                  subtitle={leg.pilotName ?? 'Unassigned'}
                />
              ))
            )}
          </div>
        )
      })}
    </div>
  )
}

function ByAircraftView({ overview }: { overview: NonNullable<ReturnType<typeof useScheduleOverview>['overview']> }) {
  const pilotIds = useMemo(() => {
    const ids = new Set<string>()
    for (const row of overview.byAircraft) {
      for (const leg of row.legs) ids.add(leg.pilotId)
    }
    return [...ids]
  }, [overview.byAircraft])

  const pilotNameById = useMemo(() => {
    const names = new Map<string, string>()
    for (const row of overview.byAircraft) {
      for (const leg of row.legs) {
        if (leg.pilotName) names.set(leg.pilotId, leg.pilotName)
      }
    }
    return names
  }, [overview.byAircraft])

  if (overview.byAircraft.length === 0) {
    return (
      <EmptyState
        icon={Plane}
        title="No fleet yet"
        description="Buy or lease an aircraft on the Fleet page to see it here."
      />
    )
  }

  const anyLegs = overview.byAircraft.some((row) => row.legs.length > 0)

  return (
    <div className="space-y-3">
      {anyLegs && pilotIds.length > 0 && (
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 rounded-md border border-border bg-muted/20 p-2.5">
          {pilotIds.map((id) => {
            const swatch = pilotSwatch(id)
            return (
              <span key={id} className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <span className={cn('size-2.5 shrink-0 rounded-full', swatch.dot)} aria-hidden="true" />
                {pilotNameById.get(id) ?? 'Unknown pilot'}
              </span>
            )
          })}
        </div>
      )}

      <Card>
        <CardContent className="overflow-x-auto p-4">
          <div className="min-w-[820px]">
            <div className="grid grid-cols-[minmax(120px,160px)_repeat(7,minmax(90px,1fr))] gap-2 border-b border-border pb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              <span>Aircraft</span>
              {DAY_DISPLAY_ORDER.map((day) => (
                <span key={day} className="text-center">
                  {DAY_SHORT_LABELS[day]}
                </span>
              ))}
            </div>
            {overview.byAircraft.map((row) => (
              <WeekRow
                key={row.fleetAircraftId}
                label={row.registration}
                sublabel={row.locationIcao}
                legs={row.legs}
                colourClass={(leg) => pilotSwatch(leg.pilotId).chip}
              />
            ))}
          </div>
        </CardContent>
      </Card>

      {!anyLegs && (
        <p className="text-center text-sm text-muted-foreground">
          No virtual pilot has a saved schedule yet - build one from the Pilots roster.
        </p>
      )}
    </div>
  )
}

function ByPilotView({ pilots }: { pilots: OverviewPilotRow[] }) {
  if (pilots.length === 0) {
    return (
      <EmptyState
        icon={Users}
        title="No virtual pilots yet"
        description="Hire a pilot to give them a standing weekly schedule."
      />
    )
  }

  const anyScheduled = pilots.some((p) => p.dutyDays.length > 0)

  return (
    <Card>
      <CardContent className="overflow-x-auto p-4">
        <div className="min-w-[820px]">
          <div className="grid grid-cols-[minmax(120px,160px)_repeat(7,minmax(90px,1fr))] gap-2 border-b border-border pb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <span>Pilot</span>
            {DAY_DISPLAY_ORDER.map((day) => (
              <span key={day} className="text-center">
                {DAY_SHORT_LABELS[day]}
              </span>
            ))}
          </div>
          {pilots.map((pilot) => {
            const allLegs = pilot.dutyDays.flatMap((d) => d.legs)
            const registrationByDay = new Map(pilot.dutyDays.map((d) => [d.dayOfWeek, d.registration]))
            return (
              <WeekRow
                key={pilot.pilotId}
                label={pilot.name}
                sublabel={pilot.dutyDays.length > 0 ? [...new Set(pilot.dutyDays.map((d) => registrationByDay.get(d.dayOfWeek)).filter(Boolean))].join(', ') : 'No standing schedule'}
                legs={allLegs}
                colourClass={() => 'border-accent/30 bg-accent/10 text-foreground'}
              />
            )
          })}
          {!anyScheduled && (
            <p className="py-3 text-center text-sm text-muted-foreground">No virtual pilot has a saved schedule yet.</p>
          )}
        </div>
      </CardContent>
    </Card>
  )
}

/**
 * Read-only, airline-wide view of the whole week - "is my fleet actually being used" (docs/PLAN.md
 * "2a"). Two toggleable views of the same underlying data: every aircraft as a row with its legs
 * colour-coded by pilot, or every pilot as a row with their own duty days. Nothing here is
 * editable - go to a pilot's own schedule (Pilots roster -> Schedule) to change anything.
 */
export function ScheduleOverview() {
  const { status, overview } = useScheduleOverview()
  const [view, setView] = useState<'aircraft' | 'pilot'>('aircraft')

  return (
    <div className="space-y-4">
      <Tabs value={view} onValueChange={(v) => setView(v as 'aircraft' | 'pilot')}>
        <TabsList>
          <TabsTrigger value="aircraft" className="gap-1.5">
            <Plane className="size-3.5" />
            By aircraft
          </TabsTrigger>
          <TabsTrigger value="pilot" className="gap-1.5">
            <Users className="size-3.5" />
            By pilot
          </TabsTrigger>
        </TabsList>

        <TabsContent value="aircraft">
          {status === 'loading' && <Skeleton className="h-64 w-full" />}
          {status === 'error' && <p className="text-sm text-danger">Could not load the schedule overview. Try again.</p>}
          {status === 'ready' && overview && <ByAircraftView overview={overview} />}
        </TabsContent>

        <TabsContent value="pilot">
          {status === 'loading' && <Skeleton className="h-64 w-full" />}
          {status === 'error' && <p className="text-sm text-danger">Could not load the schedule overview. Try again.</p>}
          {status === 'ready' && overview && <ByPilotView pilots={overview.byPilot} />}
        </TabsContent>
      </Tabs>

      {status === 'ready' && overview && overview.byAircraft.length === 0 && overview.byPilot.length === 0 && (
        <div className="flex items-center justify-center gap-1.5 py-2 text-xs text-muted-foreground">
          <LayoutGrid className="size-3.5" aria-hidden="true" />
          Nothing to show yet.
        </div>
      )}
    </div>
  )
}
