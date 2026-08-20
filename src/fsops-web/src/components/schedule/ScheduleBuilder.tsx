import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { CalendarClock, Info, Loader2, RotateCcw, Save, Sparkles } from 'lucide-react'

import { ConflictList } from './ConflictList'
import { CopyDayDialog } from './CopyDayDialog'
import {
  addLegToDay,
  draftWeekToInput,
  findLegsOrphanedByRemoval,
  findOverlappingLeg,
  removeLegAndOrphans,
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
import { OrphanedLegsDialog, type PendingRemoval } from './OrphanedLegsDialog'
import { ScheduleGrid } from './ScheduleGrid'
import { buildStarterSchedule, type StarterScheduleIssue, type StarterScheduleReason, type StarterScheduleStop } from './starterSchedule'
import { WeeklySummary } from './WeeklySummary'
import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleetLite, useSchedule, fetchAircraftOptions, fetchLegOptions } from '@/hooks/useSchedule'
import { useRoutes } from '@/hooks/useRoutes'
import { useSettings } from '@/hooks/useSettings'
import type { PilotSummary } from '@/types/pilot'
import { DAY_LABELS, type DayOfWeek } from '@/types/schedule'

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

/**
 * One sentence saying why this pilot got this route, from the generator's own structured reason -
 * see starterSchedule.ts's `scoreLegOption`. A suggestion the player cannot interrogate is a
 * suggestion they have to take on trust, and "why did FSOps put G-FWLY on Bristol - Edinburgh"
 * deserves an answer that names the money and the airframe rather than a shrug. `money` is passed
 * in from `useSettings().fmt` so the figure is shown in the player's own currency.
 */
function describeStarterScheduleReason(reason: StarterScheduleReason, money: (value: number) => string): string {
  const leg = `${reason.departureIcao} - ${reason.arrivalIcao}`
  const shared = reason.otherPilotLegs > 0
    ? ' It still wins despite sharing that market with another pilot already flying it.'
    : ''
  return `${leg} is the most profitable leg ${reason.registration} can fly from ${reason.departureIcao} - about ${money(reason.profitPerSector)} a sector.${shared}`
}

/**
 * What actually ended the fullest generated day - see starterSchedule.ts's `StarterScheduleStop`.
 *
 * The player asked for this in as many words: the old generator stopped at four legs a day while
 * they were hand-building eight through the picker, and it never said why. A suggestion that fills
 * the day and stays silent is only marginally better - they still cannot tell "this is everything
 * the rules allow" from "this is all it looked for". So the binding constraint is quoted, in the
 * backend's own words where it has any (same convention as `IllegalLegOption.reason`: render it
 * verbatim, never re-word it).
 */
function describeStarterScheduleStop(stop: StarterScheduleStop): string {
  const day = DAY_LABELS[stop.day]
  const fullest = `${day} is the fullest day at ${stop.legs} leg${stop.legs === 1 ? '' : 's'}`
  switch (stop.kind) {
    // Deliberately "came back refused" rather than "would break a rule": the sentence that follows
    // is just as often "the aircraft is already flying this for another pilot" as it is a duty-hour
    // limit, and a generated week uses one airframe throughout, so a contended aircraft is an
    // ordinary reason a day stops short rather than an edge case worth its own wording.
    case 'rule':
      return `${fullest}, and it stops there because the next leg came back refused: ${stop.reason}`
    case 'week-closure':
      return `${fullest}. There was room for one more, but it would have left the aircraft away from base with no day left to bring it home - and a weekly pattern has to end where it starts.`
    case 'calendar-day':
      return `${fullest}, which is as many as fit between the first departure and midnight.`
    case 'network':
      return `${fullest}. The check for what could follow it did not come back, so the day may be shorter than the rules allow - try again to fill it out.`
    case 'nowhere-to-go':
    default:
      return `${fullest}, and it stops there because nothing on offer could carry on from where that leg lands.`
  }
}

/** The full weekly schedule builder for one pilot: loads their saved schedule and the airline's
 *  fleet/routes, holds an editable draft (aircraft-per-duty-day), and drives the grid, the
 *  aircraft/leg picker dialog, and save. */
export function ScheduleBuilder({ pilot, onSaved }: ScheduleBuilderProps) {
  const schedule = useSchedule(pilot.id)
  const routesQuery = useRoutes()
  const fleetQuery = useFleetLite()
  const { fmt } = useSettings()

  const [week, setWeek] = useState<DraftWeek>({})
  const [savedSignature, setSavedSignature] = useState('')
  const [autoSuspendOnMaintenance, setAutoSuspendOnMaintenance] = useState(true)
  const [savedAutoSuspendOnMaintenance, setSavedAutoSuspendOnMaintenance] = useState(true)
  const [dialog, setDialog] = useState<DialogState>(DEFAULT_DIALOG)
  const [saving, setSaving] = useState(false)
  const [suggesting, setSuggesting] = useState(false)
  const [saveFailure, setSaveFailure] = useState<{ error: string; conflicts: string[] } | null>(null)
  /** Notices from the last SUCCESSFUL save - the schedule is stored and repeating, this is just
   *  something the player would want to know (today: its aircraft is not where the pattern starts,
   *  so it will not begin flying until the airframe is back). Never an error, never blocking. */
  const [saveAdvisories, setSaveAdvisories] = useState<string[]>([])
  const [suggestionIssue, setSuggestionIssue] = useState<StarterScheduleIssue | null>(null)
  /** Why the last suggestion chose what it chose - cleared the moment the draft changes, for the
   *  same reason the failure banner is (see `updateWeek`): it is a verdict on ONE draft, and a
   *  stale explanation of a week that no longer exists is worse than none. */
  const [suggestionReason, setSuggestionReason] = useState<StarterScheduleReason | null>(null)
  /** Which rule ended the fullest day of the last suggestion - cleared alongside the reason, and
   *  for the same reason: it describes ONE draft, and a stale verdict is worse than none. */
  const [suggestionStop, setSuggestionStop] = useState<StarterScheduleStop | null>(null)
  const [pendingRemoval, setPendingRemoval] = useState<PendingRemoval | null>(null)
  /** The day whose "copy to other days" dialog is open, or null. */
  const [copyingDay, setCopyingDay] = useState<DayOfWeek | null>(null)
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
    setSuggestionReason(null)
    setPendingRemoval(null)
    initializedForPilot.current = pilot.id
  }, [schedule.status, schedule.dutyDays, schedule.autoSuspendOnMaintenance, pilot.id])

  const isDirty = weekSignature(week) !== savedSignature || autoSuspendOnMaintenance !== savedAutoSuspendOnMaintenance
  const totalLegs = Object.values(week).reduce((sum, day) => sum + (day?.legs.length ?? 0), 0)

  /**
   * Every path that changes the draft goes through here rather than calling `setWeek` directly, so
   * that both banners below it are cleared in the same breath. Real-use defect, 2026-08-13: a
   * player was shown "FSOps couldn't fit a legal starter schedule together" and a save conflict
   * naming an aircraft, then changed the draft entirely - and both messages stayed on screen,
   * describing a week that no longer existed. A conflict list and a suggestion failure are both
   * verdicts on ONE specific draft; the moment that draft changes they are stale, and a stale
   * verdict is worse than none because the player cannot tell which of the two they are looking at.
   * Re-running either check is a deliberate action (Save, or Suggest again), so the honest state
   * between them is silence.
   */
  function updateWeek(next: DraftWeek | ((current: DraftWeek) => DraftWeek)) {
    setWeek(next)
    setSaveFailure(null)
    setSuggestionIssue(null)
    setSuggestionReason(null)
    setSuggestionStop(null)
    setSaveAdvisories([])
  }

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
    updateWeek((current) => setDayAircraft(current, day, option.fleetAircraftId, option.registration))
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
    updateWeek((current) => addLegToDay(current, day, leg))
  }

  function handleConfirmRetime(day: DayOfWeek, legId: string, time: string) {
    updateWeek((current) => updateLegTime(current, day, legId, time))
  }

  // Real-use defect, 2026-08-13: deleting only the outbound half of an out-and-back left the return
  // sitting in the grid looking perfectly scheduled, and the player only found out it could never
  // actually fly from a Save-time refusal worded around "the first leg of the week" - no mention of
  // the delete that caused it. findLegsOrphanedByRemoval (draftEntry.ts) walks the whole chain for
  // this aircraft and reports every leg the removal would strand, not just the one next to it - see
  // its own doc. The common case (removing a leg nothing else depends on) still removes immediately,
  // exactly as before; only a removal with real consequences elsewhere in the week stops for a
  // choice, via OrphanedLegsDialog below.
  function handleRemove(day: DayOfWeek, legId: string) {
    const dayEntry = week[day]
    const leg = dayEntry?.legs.find((candidate) => candidate.id === legId)
    if (!dayEntry || !leg) return

    const aircraftLocationIcao = fleetQuery.fleet.find((aircraft) => aircraft.id === dayEntry.fleetAircraftId)?.locationIcao
    const orphans = findLegsOrphanedByRemoval(week, day, legId, aircraftLocationIcao)

    if (orphans.length === 0) {
      updateWeek((current) => removeLegAndOrphans(current, day, legId, []))
      return
    }

    setPendingRemoval({ day, leg, orphans })
  }

  function confirmPendingRemoval() {
    if (!pendingRemoval) return
    const { day, leg, orphans } = pendingRemoval
    updateWeek((current) => removeLegAndOrphans(current, day, leg.id, orphans))
    setPendingRemoval(null)
  }

  function cancelPendingRemoval() {
    setPendingRemoval(null)
  }

  /** The player has already been shown, day by day, what this replaces and what it breaks (see
   *  CopyDayDialog) - so applying it here is completing the thing they just agreed to, and it lands
   *  in the draft, where Discard changes still undoes it. */
  function handleCopyDayConfirm(next: DraftWeek) {
    const copiedFrom = copyingDay
    setCopyingDay(null)
    updateWeek(next)
    if (copiedFrom !== null) {
      toast.success(`Copied ${DAY_LABELS[copiedFrom]} - review and save when ready.`)
    }
  }

  function handleDiscard() {
    setWeek(scheduleToDraftWeek(schedule.dutyDays))
    setAutoSuspendOnMaintenance(savedAutoSuspendOnMaintenance)
    setSaveFailure(null)
  }

  async function handleSave() {
    setSaving(true)
    setSaveFailure(null)
    setSaveAdvisories([])
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
        setSaveAdvisories(result.advisories)
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
    setSuggestionReason(null)
    setSuggestionStop(null)
    try {
      const outcome = await buildStarterSchedule(routesQuery.routes, {
        fetchAircraftOptions: (day) => fetchAircraftOptions(pilot.id, day),
        fetchLegOptions: (day, time, fleetAircraftId, draftDutyDays) => fetchLegOptions(pilot.id, day, time, fleetAircraftId, draftDutyDays),
      })
      if (!outcome.ok) {
        setSuggestionIssue(outcome.issue)
        return
      }
      setWeek(outcome.result.week)
      setSuggestionReason(outcome.result.reason)
      setSuggestionStop(outcome.result.stop)
      const { legsAdded, daysUsed, blockMinutes } = outcome.result
      // Block hours, not just a leg count: "hours of flying" is the unit the player measures this
      // feature in, and it is the number that makes two suggestions comparable when one is short
      // sectors and the other is long ones.
      const hours = (blockMinutes / 60).toFixed(1)
      toast.success(
        `Added ${legsAdded} leg${legsAdded === 1 ? '' : 's'} across ${daysUsed} day${daysUsed === 1 ? '' : 's'}, ${hours} hours in the air - review and save when ready.`,
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

      {/* A saved schedule that will not start flying yet. Deliberately styled as a notice rather
          than a conflict: nothing failed, nothing needs fixing, and the pattern repeats forever -
          but under True-life every occurrence the aircraft cannot reach costs a cancellation fee,
          so saying nothing at all would trade a confusing refusal for a silent bleed. */}
      {saveAdvisories.length > 0 && (
        <div className="space-y-1 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
          {saveAdvisories.map((advisory, index) => (
            <p key={index} className="flex items-start gap-2">
              <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">{advisory}</span>
            </p>
          ))}
        </div>
      )}

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

      {/* Why the suggestion picked this route for this aircraft. A notice, never a warning:
          nothing is wrong, the player is simply owed the reasoning behind a week someone else
          built for them - and it is the thing that makes "why did my two pilots get different
          routes" answerable at a glance. */}
      {suggestionReason && (
        <div
          data-testid="starter-schedule-reason"
          className="flex items-start gap-2 rounded-md border border-border bg-muted/40 p-3 text-sm text-muted-foreground"
        >
          <Sparkles className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          <span className="min-w-0 break-words">
            {describeStarterScheduleReason(suggestionReason, fmt.money)}
            {suggestionStop && <> {describeStarterScheduleStop(suggestionStop)}</>}
          </span>
        </div>
      )}

      {/* The same notice when there is no profit figure to explain the ROUTE with, but there is
          still an honest answer to "why does the week stop here". Without this, an airline whose
          world data cannot be priced gets a filled week and complete silence about its limits -
          which is the state that produced the original complaint. */}
      {!suggestionReason && suggestionStop && (
        <div
          data-testid="starter-schedule-stop"
          className="flex items-start gap-2 rounded-md border border-border bg-muted/40 p-3 text-sm text-muted-foreground"
        >
          <Sparkles className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          <span className="min-w-0 break-words">{describeStarterScheduleStop(suggestionStop)}</span>
        </div>
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
          onCopyDayClick={setCopyingDay}
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

      <CopyDayDialog
        sourceDay={copyingDay}
        week={week}
        pilotId={pilot.id}
        onClose={() => setCopyingDay(null)}
        onConfirm={handleCopyDayConfirm}
      />

      <OrphanedLegsDialog pending={pendingRemoval} onCancel={cancelPendingRemoval} onConfirm={confirmPendingRemoval} />
    </div>
  )
}
