import { useEffect, useMemo, useRef, useState } from 'react'
import { toast } from 'sonner'
import { CalendarClock, Loader2, RotateCcw, Save, Sparkles } from 'lucide-react'

import { ConflictList } from './ConflictList'
import {
  draftFromOption,
  draftToScheduleInputs,
  entriesToDraft,
  findOverlappingEntry,
  type DraftEntry,
} from './draftEntry'
import { FleetAvailabilityPanel } from './FleetAvailabilityPanel'
import { LegDialog } from './LegDialog'
import { RoutePalette } from './RoutePalette'
import { ScheduleGrid } from './ScheduleGrid'
import type { RouteLike } from './types'
import { WeeklySummary } from './WeeklySummary'
import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleetLite, useSchedule, fetchScheduleOptions } from '@/hooks/useSchedule'
import { useRoutes } from '@/hooks/useRoutes'
import { useRouteBlockTimes } from '@/hooks/useRouteBlockTimes'
import type { PilotSummary } from '@/types/pilot'
import { minutesToTime, timeToMinutes, type DayOfWeek } from '@/types/schedule'

interface ScheduleBuilderProps {
  pilot: PilotSummary
  /** Called after a successful save so the caller can refresh the pilot list (sectorsPerWeek /
   *  weekly estimates live on the pilot record, not this component). */
  onSaved?: () => void
}

interface DialogState {
  open: boolean
  mode: 'add' | 'edit'
  day: DayOfWeek
  time: string
  routeId?: string
  entry?: DraftEntry
}

const DEFAULT_DIALOG: DialogState = { open: false, mode: 'add', day: 1, time: '08:00' }

/** Weekdays and a plausible morning departure - the starting point for the "suggested schedule"
 *  offer on an empty week. It only ever proposes what the backend confirms is actually legal, and
 *  builds a same-day out-and-back per day (never a dangling one-way leg) so every day the
 *  suggestion proposes already closes on its own - the whole week is trivially closed too. */
const STARTER_DAYS: DayOfWeek[] = [1, 2, 3, 4, 5]
const STARTER_TIME = '08:00'
/** Gap offered between the suggested outbound's arrival and the suggested return's departure.
 *  Just a starting guess - if it's tighter than the real minimum turnaround, options simply won't
 *  offer a legal return for that day and the suggestion skips it rather than proposing something
 *  that would fail at save. */
const RETURN_BUFFER_MINUTES = 45

function signature(entries: { dayOfWeek: number; departureTimeUtc: string; routeId: string; fleetAircraftId: string }[]): string {
  return entries
    .map((e) => `${e.dayOfWeek}|${e.departureTimeUtc}|${e.routeId}|${e.fleetAircraftId}`)
    .sort()
    .join(';')
}

/** The full weekly schedule builder for one pilot: loads their saved schedule and the airline's
 *  routes/fleet, holds an editable draft, and drives the grid, the add/edit dialog, and save. */
export function ScheduleBuilder({ pilot, onSaved }: ScheduleBuilderProps) {
  const schedule = useSchedule(pilot.id)
  const routesQuery = useRoutes()
  const blockMinutesByRouteId = useRouteBlockTimes(routesQuery.routes)
  const fleetQuery = useFleetLite()

  const [draft, setDraft] = useState<DraftEntry[]>([])
  const [savedSignature, setSavedSignature] = useState('')
  const [dialog, setDialog] = useState<DialogState>(DEFAULT_DIALOG)
  const [saving, setSaving] = useState(false)
  const [suggesting, setSuggesting] = useState(false)
  const [saveFailure, setSaveFailure] = useState<{ error: string; conflicts: string[] } | null>(null)
  const initializedForPilot = useRef<string | null>(null)

  const fleetById = useMemo(() => new Map(fleetQuery.fleet.map((a) => [a.id, a])), [fleetQuery.fleet])
  const reservedAircraftIds = useMemo(
    () => new Set(fleetQuery.fleet.filter((a) => a.reservedForPlayer).map((a) => a.id)),
    [fleetQuery.fleet],
  )

  const routes: RouteLike[] = useMemo(
    () =>
      routesQuery.routes.map((route) => ({
        id: route.id,
        departureIcao: route.departureIcao,
        arrivalIcao: route.arrivalIcao,
        flightNumber: route.flightNumber,
        estimatedBlockMinutes: route.estimatedBlockMinutes ?? blockMinutesByRouteId[route.id] ?? null,
      })),
    [routesQuery.routes, blockMinutesByRouteId],
  )
  const routeById = useMemo(() => new Map(routes.map((r) => [r.id, r])), [routes])

  // Initialise (or re-initialise, on pilot switch) the editable draft once both the schedule and
  // the fleet have loaded - registration lookups need the fleet, so this waits for both rather
  // than racing entriesToDraft against an empty fleetById.
  useEffect(() => {
    if (schedule.status !== 'ready' || fleetQuery.status !== 'ready') return
    if (initializedForPilot.current === pilot.id) return
    const nextDraft = entriesToDraft(schedule.entries, fleetById)
    setDraft(nextDraft)
    setSavedSignature(signature(schedule.entries))
    initializedForPilot.current = pilot.id
  }, [schedule.status, schedule.entries, fleetQuery.status, fleetById, pilot.id])

  const isDirty = signature(draftToScheduleInputs(draft)) !== savedSignature

  function openAddDialog(day: DayOfWeek, time = '08:00', routeId?: string) {
    setDialog({ open: true, mode: 'add', day, time, routeId })
  }

  function openEditDialog(entry: DraftEntry) {
    setDialog({ open: true, mode: 'edit', day: entry.dayOfWeek, time: entry.departureTimeUtc.slice(0, 5), entry })
  }

  function handleConfirmAdd(entry: DraftEntry) {
    const overlap = findOverlappingEntry(entry, draft)
    if (overlap) {
      toast.error(
        `That slot overlaps ${overlap.flightNumber ?? 'another leg'} (${overlap.departureIcao} → ${overlap.arrivalIcao}). Pick a different time.`,
      )
      return
    }
    setDraft((current) => [...current, entry])
  }

  function handleConfirmMove(entryId: string, day: DayOfWeek, time: string) {
    setDraft((current) => current.map((e) => (e.id === entryId ? { ...e, dayOfWeek: day, departureTimeUtc: time } : e)))
  }

  function handleRemove(entryId: string) {
    setDraft((current) => current.filter((e) => e.id !== entryId))
  }

  function handleDiscard() {
    setDraft(entriesToDraft(schedule.entries, fleetById))
    setSaveFailure(null)
  }

  async function handleSave() {
    setSaving(true)
    setSaveFailure(null)
    try {
      const result = await schedule.save(draftToScheduleInputs(draft))
      if (result.ok) {
        setDraft(entriesToDraft(result.entries, fleetById))
        setSavedSignature(signature(result.entries))
        toast.success('Schedule saved.')
        onSaved?.()
      } else {
        setSaveFailure({ error: result.error, conflicts: result.conflicts })
        toast.error('This schedule has conflicts - see below.')
      }
    } finally {
      setSaving(false)
    }
  }

  async function handleUseSuggestion() {
    setSuggesting(true)
    try {
      const built: DraftEntry[] = []
      let daysProposed = 0
      for (const day of STARTER_DAYS) {
        try {
          // eslint-disable-next-line no-await-in-loop
          const outboundOptions = await fetchScheduleOptions(pilot.id, day, STARTER_TIME, draftToScheduleInputs(built))
          const outboundPick = outboundOptions.legal.find((option) => !findOverlappingEntry(
            { dayOfWeek: day, departureTimeUtc: `${STARTER_TIME}:00`, blockMinutes: routeById.get(option.routeId)?.estimatedBlockMinutes ?? 60 },
            built,
          ))
          if (!outboundPick) continue

          const outboundRoute = routeById.get(outboundPick.routeId)
          const outboundBlock = outboundRoute?.estimatedBlockMinutes ?? 60
          const outboundEntry = draftFromOption(outboundPick, day, STARTER_TIME, outboundRoute)
          const tentative = [...built, outboundEntry]

          const returnTime = minutesToTime(
            timeToMinutes(`${STARTER_TIME}:00`) + outboundBlock + RETURN_BUFFER_MINUTES,
          ).slice(0, 5)

          // eslint-disable-next-line no-await-in-loop
          const returnOptions = await fetchScheduleOptions(pilot.id, day, returnTime, draftToScheduleInputs(tentative))
          const returnPick = returnOptions.legal.find(
            (option) =>
              option.fleetAircraftId === outboundPick.fleetAircraftId &&
              option.departureIcao === outboundPick.arrivalIcao &&
              option.arrivalIcao === outboundPick.departureIcao &&
              !findOverlappingEntry(
                { dayOfWeek: day, departureTimeUtc: `${returnTime}:00`, blockMinutes: routeById.get(option.routeId)?.estimatedBlockMinutes ?? 60 },
                tentative,
              ),
          )
          // Only ever proposes a same-day out-and-back, never a dangling one-way leg - a lone
          // outbound with no return would fail week-closure at save, and the whole point of a
          // suggestion is that it can be saved as-is.
          if (returnPick) {
            built.push(outboundEntry)
            built.push(draftFromOption(returnPick, day, returnTime, routeById.get(returnPick.routeId)))
            daysProposed += 1
          }
          // eslint-disable-next-line no-empty
        } catch {}
      }
      if (built.length === 0) {
        toast.error('No legal starter schedule could be found - build one manually below.')
      } else {
        setDraft(built)
        toast.success(`Added an out-and-back on ${daysProposed} day${daysProposed === 1 ? '' : 's'} - review and save when ready.`)
      }
    } finally {
      setSuggesting(false)
    }
  }

  const loading = schedule.status === 'loading' || fleetQuery.status === 'loading' || routesQuery.status === 'loading'

  if (loading && draft.length === 0 && initializedForPilot.current !== pilot.id) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-[480px] w-full" />
      </div>
    )
  }

  if (schedule.status === 'error') {
    return <p className="text-sm text-danger">Could not load this pilot's schedule. Check your connection and try again.</p>
  }

  return (
    <div className="space-y-4">
      <WeeklySummary draft={draft} pilot={pilot} isDirty={isDirty} />

      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <CalendarClock className="size-3.5 shrink-0" aria-hidden="true" />
          This week repeats indefinitely - {pilot.name} flies this pattern every week until you change it.
        </p>
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={handleDiscard} disabled={!isDirty || saving}>
            <RotateCcw className="size-3.5" />
            Discard changes
          </Button>
          <Button type="button" size="sm" onClick={handleSave} disabled={saving || !isDirty}>
            {saving ? <Loader2 className="size-3.5 animate-spin" /> : <Save className="size-3.5" />}
            {saving ? 'Saving…' : 'Save schedule'}
          </Button>
        </div>
      </div>

      {saveFailure && <ConflictList error={saveFailure.error} conflicts={saveFailure.conflicts} />}

      {draft.length === 0 && (
        <EmptyState
          icon={Sparkles}
          title="No legs scheduled yet"
          description={`A blank week is the hardest part of any planner - drag a route onto the grid below, or let us suggest a starter pattern for ${pilot.name} that you can adjust before saving.`}
          action={
            <Button type="button" onClick={handleUseSuggestion} disabled={suggesting}>
              {suggesting ? <Loader2 className="size-4 animate-spin" /> : <Sparkles className="size-4" />}
              Suggest a starter schedule
            </Button>
          }
        />
      )}

      <div className="grid gap-4 lg:grid-cols-[1fr_260px]">
        <ScheduleGrid
          draft={draft}
          reservedAircraftIds={reservedAircraftIds}
          onDropRoute={(day, time, routeId) => openAddDialog(day, time, routeId)}
          onMoveEntry={handleConfirmMove}
          onSelectEntry={openEditDialog}
          onAddClick={(day) => openAddDialog(day)}
        />
        <div className="space-y-4">
          <RoutePalette routes={routes} status={routesQuery.status} onSelectRoute={(routeId) => openAddDialog(1, '08:00', routeId)} />
          <FleetAvailabilityPanel draft={draft} fleet={fleetQuery.fleet} />
        </div>
      </div>

      <LegDialog
        open={dialog.open}
        onOpenChange={(open) => setDialog((current) => ({ ...current, open }))}
        pilotId={pilot.id}
        mode={dialog.mode}
        initialDay={dialog.day}
        initialTime={dialog.time}
        initialRouteId={dialog.routeId}
        editingEntry={dialog.entry}
        routes={routes}
        fleetById={fleetById}
        draft={draft}
        onConfirmAdd={handleConfirmAdd}
        onConfirmMove={handleConfirmMove}
        onRemove={handleRemove}
      />
    </div>
  )
}
