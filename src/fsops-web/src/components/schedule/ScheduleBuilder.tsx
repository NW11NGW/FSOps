import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { CalendarClock, Info, Loader2, RotateCcw, Save, Sparkles } from 'lucide-react'

import { ConflictList } from './ConflictList'
import {
  addLegToDay,
  clearDay,
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
import { buildStarterSchedule, type StarterScheduleIssue } from './starterSchedule'
import { WeeklySummary } from './WeeklySummary'
import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleetLite, useSchedule, fetchAircraftOptions, fetchLegOptions } from '@/hooks/useSchedule'
import { useRoutes } from '@/hooks/useRoutes'
import type { PilotSummary } from '@/types/pilot'
import type { DayOfWeek } from '@/types/schedule'

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

/**
 * Copy for every way "Suggest a starter schedule" can come back without a schedule - see
 * starterSchedule.ts's own doc for what each of these actually means. Named preconditions point
 * straight at the page that fixes them; `no-legal-schedule` is the one genuine "the constraints
 * don't line up" case, worded as the app not having found an arrangement rather than the player
 * having done something wrong, and `check-failed` covers the network call itself failing.
 */
function describeStarterScheduleIssue(issue: StarterScheduleIssue): { message: string; linkTo?: string; linkLabel?: string } {
  switch (issue.kind) {
    case 'no-routes':
      return {
        message: "You don't have any routes yet - a starter schedule needs at least one to build from.",
        linkTo: '/routes',
        linkLabel: 'Go to Routes',
      }
    case 'no-aircraft':
      return {
        message: "You don't have any aircraft in your fleet yet - a starter schedule needs one to fly.",
        linkTo: '/fleet',
        linkLabel: 'Go to Fleet',
      }
    case 'all-reserved':
      return {
        message: 'Every aircraft in your fleet is reserved for you to fly - release at least one so a virtual pilot can use it.',
        linkTo: '/fleet',
        linkLabel: 'Go to Fleet',
      }
    case 'no-usable-aircraft':
      return {
        message: "None of your aircraft can be scheduled right now - check what's holding each one back.",
        linkTo: '/fleet',
        linkLabel: 'Go to Fleet',
      }
    case 'check-failed':
      return { message: "Could not check what's available. Check your connection and try again." }
    case 'no-legal-schedule':
    default:
      return {
        message: "FSOps couldn't fit a legal starter schedule together from what's currently available - build one manually below.",
      }
  }
}

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
  const [suggestionIssue, setSuggestionIssue] = useState<StarterScheduleIssue | null>(null)
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
    setSuggestionIssue(null)
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
    setSuggestionIssue(null)
    try {
      const outcome = await buildStarterSchedule(routesQuery.routes.length, {
        fetchAircraftOptions: (day) => fetchAircraftOptions(pilot.id, day),
        fetchLegOptions: (day, time, fleetAircraftId, draftDutyDays) => fetchLegOptions(pilot.id, day, time, fleetAircraftId, draftDutyDays),
      })
      if (!outcome.ok) {
        setSuggestionIssue(outcome.issue)
        return
      }
      setWeek(outcome.result.week)
      const { legsAdded, daysUsed } = outcome.result
      toast.success(
        `Added ${legsAdded} leg${legsAdded === 1 ? '' : 's'} across ${daysUsed} day${daysUsed === 1 ? '' : 's'} - review and save when ready.`,
      )
    } catch {
      setSuggestionIssue({ kind: 'check-failed' })
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

  const suggestionIssueDescription = suggestionIssue ? describeStarterScheduleIssue(suggestionIssue) : null

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

      {suggestionIssueDescription && (
        <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
          <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          <span className="min-w-0 break-words">
            {suggestionIssueDescription.message}
            {suggestionIssueDescription.linkTo && (
              <>
                {' '}
                <Link to={suggestionIssueDescription.linkTo} className="font-medium underline-offset-2 hover:underline">
                  {suggestionIssueDescription.linkLabel}
                </Link>
              </>
            )}
          </span>
        </div>
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
