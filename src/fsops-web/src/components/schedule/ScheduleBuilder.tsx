import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import { CalendarClock, Loader2, RotateCcw, Save, Sparkles } from 'lucide-react'

import { ConflictList } from './ConflictList'
import {
  addLegToDay,
  clearDay,
  draftLegFromOption,
  draftWeekToInput,
  findOverlappingLeg,
  removeLegFromDay,
  scheduleToDraftWeek,
  setDayAircraft,
  updateLegTime,
  weekSignature,
  type DraftLeg,
  type DraftWeek,
} from './draftEntry'
import { FleetAvailabilityPanel } from './FleetAvailabilityPanel'
import { LegDialog } from './LegDialog'
import { MaintenanceSuspendToggle } from './MaintenanceSuspendToggle'
import { ScheduleGrid } from './ScheduleGrid'
import { WeeklySummary } from './WeeklySummary'
import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleetLite, useSchedule, fetchAircraftOptions, fetchLegOptions } from '@/hooks/useSchedule'
import { useRoutes } from '@/hooks/useRoutes'
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
  editingLeg?: DraftLeg
  forceAircraftStep?: boolean
}

const DEFAULT_DIALOG: DialogState = { open: false, mode: 'add', day: 1, time: '08:00' }

/** Weekdays and a plausible morning departure - the starting point for the "suggested schedule"
 *  offer on an empty week. It only ever proposes what the backend confirms is actually legal
 *  (aircraft first, then a same-day out-and-back on it), and never proposes a dangling one-way leg
 *  so every day it suggests already closes on its own - the whole week is trivially closed too. */
const STARTER_DAYS: DayOfWeek[] = [1, 2, 3, 4, 5]
const STARTER_TIME = '08:00'
/** Gap offered between the suggested outbound's arrival and the suggested return's departure.
 *  Just a starting guess - if it's tighter than the real minimum turnaround, leg-options simply
 *  won't offer a legal return for that day and the suggestion skips it rather than proposing
 *  something that would fail at save. */
const RETURN_BUFFER_MINUTES = 45

/** The full weekly schedule builder for one pilot: loads their saved schedule and the airline's
 *  fleet/routes, holds an editable draft (aircraft-per-duty-day), and drives the grid, the
 *  aircraft/leg picker dialog, and save. */
export function ScheduleBuilder({ pilot, onSaved }: ScheduleBuilderProps) {
  const schedule = useSchedule(pilot.id)
  const routesQuery = useRoutes()
  const fleetQuery = useFleetLite()

  const [week, setWeek] = useState<DraftWeek>({})
  const [savedSignature, setSavedSignature] = useState('')
  const [autoSuspendOnMaintenance, setAutoSuspendOnMaintenance] = useState(true)
  const [savedAutoSuspendOnMaintenance, setSavedAutoSuspendOnMaintenance] = useState(true)
  const [dialog, setDialog] = useState<DialogState>(DEFAULT_DIALOG)
  const [saving, setSaving] = useState(false)
  const [suggesting, setSuggesting] = useState(false)
  const [saveFailure, setSaveFailure] = useState<{ error: string; conflicts: string[] } | null>(null)
  const initializedForPilot = useRef<string | null>(null)

  // Initialise (or re-initialise, on pilot switch) the editable draft once the schedule has
  // loaded.
  useEffect(() => {
    if (schedule.status !== 'ready') return
    if (initializedForPilot.current === pilot.id) return
    const nextWeek = scheduleToDraftWeek(schedule.dutyDays)
    setWeek(nextWeek)
    setSavedSignature(weekSignature(nextWeek))
    setAutoSuspendOnMaintenance(schedule.autoSuspendOnMaintenance)
    setSavedAutoSuspendOnMaintenance(schedule.autoSuspendOnMaintenance)
    initializedForPilot.current = pilot.id
  }, [schedule.status, schedule.dutyDays, schedule.autoSuspendOnMaintenance, pilot.id])

  const isDirty = weekSignature(week) !== savedSignature || autoSuspendOnMaintenance !== savedAutoSuspendOnMaintenance
  const totalLegs = Object.values(week).reduce((sum, day) => sum + (day?.legs.length ?? 0), 0)

  function openAddDialog(day: DayOfWeek, time = '08:00') {
    setDialog({ open: true, mode: 'add', day, time })
  }

  function openChangeAircraftDialog(day: DayOfWeek) {
    setDialog({ open: true, mode: 'add', day, time: '08:00', forceAircraftStep: true })
  }

  function openEditDialog(day: DayOfWeek, leg: DraftLeg) {
    setDialog({ open: true, mode: 'edit', day, time: leg.departureTimeUtc.slice(0, 5), editingLeg: leg })
  }

  function handleSetAircraft(day: DayOfWeek, option: { fleetAircraftId: string; registration: string }) {
    setWeek((current) => setDayAircraft(current, day, option.fleetAircraftId, option.registration))
  }

  function handleConfirmAdd(day: DayOfWeek, leg: DraftLeg) {
    const dayLegs = week[day]?.legs ?? []
    const overlap = findOverlappingLeg(leg, dayLegs)
    if (overlap) {
      toast.error(
        `That slot overlaps ${overlap.flightNumber ?? 'another leg'} (${overlap.departureIcao} → ${overlap.arrivalIcao}). Pick a different time.`,
      )
      return
    }
    setWeek((current) => addLegToDay(current, day, leg))
  }

  function handleConfirmRetime(day: DayOfWeek, legId: string, time: string) {
    setWeek((current) => updateLegTime(current, day, legId, time))
  }

  function handleRemove(day: DayOfWeek, legId: string) {
    setWeek((current) => {
      const withoutLeg = removeLegFromDay(current, day, legId)
      // Clearing the last leg of a day leaves its aircraft chosen but idle - harmless, but a day
      // with nothing on it and no aircraft either is one fewer stray state to reason about, so an
      // emptied day's aircraft is released too.
      return (withoutLeg[day]?.legs.length ?? 0) === 0 ? clearDay(withoutLeg, day) : withoutLeg
    })
  }

  function handleDiscard() {
    setWeek(scheduleToDraftWeek(schedule.dutyDays))
    setAutoSuspendOnMaintenance(savedAutoSuspendOnMaintenance)
    setSaveFailure(null)
  }

  async function handleSave() {
    setSaving(true)
    setSaveFailure(null)
    try {
      // autoSuspendOnMaintenance is always sent explicitly - the backend resets an omitted value
      // to true, so leaving it out here would silently switch a player's "off" choice back on the
      // next time they save anything else about this schedule.
      const result = await schedule.save(draftWeekToInput(week), autoSuspendOnMaintenance)
      if (result.ok) {
        const nextWeek = scheduleToDraftWeek(result.dutyDays)
        setWeek(nextWeek)
        setSavedSignature(weekSignature(nextWeek))
        setAutoSuspendOnMaintenance(result.autoSuspendOnMaintenance)
        setSavedAutoSuspendOnMaintenance(result.autoSuspendOnMaintenance)
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
      let built: DraftWeek = {}
      let daysProposed = 0
      for (const day of STARTER_DAYS) {
        try {
          // eslint-disable-next-line no-await-in-loop
          const aircraftOptions = await fetchAircraftOptions(pilot.id, day)
          const chosen = aircraftOptions.options.find((o) => o.eligible)
          if (!chosen) continue

          const withAircraft = setDayAircraft(built, day, chosen.fleetAircraftId, chosen.registration)

          // eslint-disable-next-line no-await-in-loop
          const outboundOptions = await fetchLegOptions(pilot.id, day, STARTER_TIME, chosen.fleetAircraftId, draftWeekToInput(withAircraft))
          const outboundPick = outboundOptions.legal[0]
          if (!outboundPick) continue

          // outboundPick.blockMinutes is already resolved against THIS day's chosen aircraft (see
          // GetLegOptionsAsync) - never a route-level default from a different aircraft type (K34).
          const outboundBlock = outboundPick.blockMinutes ?? 60
          const outboundLeg = draftLegFromOption(outboundPick, STARTER_TIME, outboundBlock)
          const withOutbound = addLegToDay(withAircraft, day, outboundLeg)

          const returnTime = minutesToTime(timeToMinutes(`${STARTER_TIME}:00`) + outboundBlock + RETURN_BUFFER_MINUTES).slice(0, 5)

          // eslint-disable-next-line no-await-in-loop
          const returnOptions = await fetchLegOptions(pilot.id, day, returnTime, chosen.fleetAircraftId, draftWeekToInput(withOutbound))
          const returnPick = returnOptions.legal.find(
            (option) => option.departureIcao === outboundPick.arrivalIcao && option.arrivalIcao === outboundPick.departureIcao,
          )
          // Only ever proposes a same-day out-and-back, never a dangling one-way leg - a lone
          // outbound with no return would fail week-closure at save, and the whole point of a
          // suggestion is that it can be saved as-is.
          if (returnPick) {
            const returnBlock = returnPick.blockMinutes ?? 60
            const returnLeg = draftLegFromOption(returnPick, returnTime, returnBlock)
            built = addLegToDay(withOutbound, day, returnLeg)
            daysProposed += 1
          }
          // eslint-disable-next-line no-empty
        } catch {}
      }
      if (daysProposed === 0) {
        toast.error('No legal starter schedule could be found - build one manually below.')
      } else {
        setWeek(built)
        toast.success(`Added an out-and-back on ${daysProposed} day${daysProposed === 1 ? '' : 's'} - review and save when ready.`)
      }
    } finally {
      setSuggesting(false)
    }
  }

  const loading = schedule.status === 'loading' || routesQuery.status === 'loading'

  if (loading && initializedForPilot.current !== pilot.id) {
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
      <WeeklySummary week={week} pilot={pilot} isDirty={isDirty} />

      <MaintenanceSuspendToggle value={autoSuspendOnMaintenance} onChange={setAutoSuspendOnMaintenance} disabled={saving} />

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

      {totalLegs === 0 && (
        <EmptyState
          icon={Sparkles}
          title="No legs scheduled yet"
          description={`A blank week is the hardest part of any planner - click a day below to pick an aircraft and its first leg, or let us suggest a starter pattern for ${pilot.name} that you can adjust before saving.`}
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
          week={week}
          onAddClick={(day, time) => openAddDialog(day, time)}
          onChangeAircraftClick={openChangeAircraftDialog}
          onMoveLeg={handleConfirmRetime}
          onSelectLeg={openEditDialog}
        />
        <FleetAvailabilityPanel week={week} fleet={fleetQuery.fleet} />
      </div>

      <LegDialog
        open={dialog.open}
        onOpenChange={(open) => setDialog((current) => ({ ...current, open }))}
        pilotId={pilot.id}
        mode={dialog.mode}
        day={dialog.day}
        initialTime={dialog.time}
        editingLeg={dialog.editingLeg}
        forceAircraftStep={dialog.forceAircraftStep}
        week={week}
        onSetAircraft={handleSetAircraft}
        onConfirmAdd={handleConfirmAdd}
        onConfirmRetime={handleConfirmRetime}
        onRemove={handleRemove}
      />
    </div>
  )
}
