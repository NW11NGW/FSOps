import { useEffect, useState } from 'react'
import { Plane, TriangleAlert } from 'lucide-react'

import { AircraftPicker } from './AircraftPicker'
import { draftLegFromOption, draftWeekToInput, findOverlappingLeg, type DraftDay, type DraftLeg, type DraftWeek } from './draftEntry'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { fetchAircraftOptions, fetchLegOptions } from '@/hooks/useSchedule'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import { minutesToTime, timeToMinutes, type AircraftOption, type AircraftOptionsResponse, type DayOfWeek, type LegalLegOption, type LegOptionsResponse } from '@/types/schedule'

type Step = 'aircraft' | 'leg'

interface LegDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  pilotId: string
  /** "add" fetches the two-step aircraft/leg picker. "edit" only lets the player retime or remove
   *  a leg it already refers to - the aircraft belongs to the whole duty day and isn't changed
   *  here (use the day's "Change aircraft" action instead). */
  mode: 'add' | 'edit'
  day: DayOfWeek
  /** "HH:mm" - ignored in edit mode (the leg's own time is the starting point). */
  initialTime: string
  editingLeg?: DraftLeg
  /** True when the day's aircraft step should show even if one is already chosen - the explicit
   *  "Change aircraft" entry point. */
  forceAircraftStep?: boolean
  week: DraftWeek
  blockMinutesByRouteId: Record<string, number | undefined>
  onSetAircraft: (day: DayOfWeek, option: AircraftOption) => void
  onConfirmAdd: (day: DayOfWeek, leg: DraftLeg) => void
  onConfirmRetime: (day: DayOfWeek, legId: string, time: string) => void
  onRemove: (day: DayOfWeek, legId: string) => void
}

/**
 * The keyboard- and mouse-accessible way to build a duty day: pick the aircraft first, then the
 * legs that aircraft can actually fly at a chosen time - the old
 * route x aircraft cross-product picker is gone; there is no field anywhere on the wire that could
 * even represent it any more.
 */
export function LegDialog({
  open,
  onOpenChange,
  pilotId,
  mode,
  day,
  initialTime,
  editingLeg,
  forceAircraftStep,
  week,
  blockMinutesByRouteId,
  onSetAircraft,
  onConfirmAdd,
  onConfirmRetime,
  onRemove,
}: LegDialogProps) {
  const currentDay: DraftDay | undefined = week[day]

  const [step, setStep] = useState<Step>('aircraft')
  const [time, setTime] = useState(initialTime)
  const [activeAircraftId, setActiveAircraftId] = useState<string | null>(null)
  const [pendingAircraft, setPendingAircraft] = useState<AircraftOption | null>(null)
  const [moveError, setMoveError] = useState<string | null>(null)

  const [aircraftOptions, setAircraftOptions] = useState<AircraftOptionsResponse | null>(null)
  const [aircraftStatus, setAircraftStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')
  const [legOptions, setLegOptions] = useState<LegOptionsResponse | null>(null)
  const [legStatus, setLegStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')

  const debouncedTime = useDebouncedValue(time, 250)

  // Reset on open - decide the starting step from what's already true of this day: if it has no
  // aircraft yet, or the caller explicitly asked to change it, start at "aircraft"; otherwise the
  // aircraft is already fixed and there's nothing to pick, so go straight to "leg".
  useEffect(() => {
    if (!open) return
    setTime(mode === 'edit' && editingLeg ? editingLeg.departureTimeUtc.slice(0, 5) : initialTime)
    setMoveError(null)
    setPendingAircraft(null)
    if (mode === 'edit') {
      setActiveAircraftId(currentDay?.fleetAircraftId ?? null)
      setStep('leg')
      return
    }
    const needsAircraft = forceAircraftStep || !currentDay
    setActiveAircraftId(currentDay?.fleetAircraftId ?? null)
    setStep(needsAircraft ? 'aircraft' : 'leg')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, day, mode, editingLeg?.id, forceAircraftStep])

  useEffect(() => {
    if (!open || mode !== 'add' || step !== 'aircraft') return
    let cancelled = false
    setAircraftStatus('loading')
    fetchAircraftOptions(pilotId, day)
      .then((result) => {
        if (cancelled) return
        setAircraftOptions(result)
        setAircraftStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setAircraftStatus('error')
      })
    return () => {
      cancelled = true
    }
  }, [open, mode, step, pilotId, day])

  useEffect(() => {
    if (!open || mode !== 'add' || step !== 'leg' || !activeAircraftId) return
    if (!/^\d{2}:\d{2}$/.test(debouncedTime)) return
    let cancelled = false
    setLegStatus('loading')
    fetchLegOptions(pilotId, day, debouncedTime, activeAircraftId, draftWeekToInput(week))
      .then((result) => {
        if (cancelled) return
        setLegOptions(result)
        setLegStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setLegStatus('error')
      })
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, mode, step, pilotId, day, activeAircraftId, debouncedTime, week])

  function handlePickAircraft(option: AircraftOption) {
    const hasLegsUnderDifferentAircraft = Boolean(currentDay) && currentDay!.fleetAircraftId !== option.fleetAircraftId && currentDay!.legs.length > 0
    if (hasLegsUnderDifferentAircraft) {
      setPendingAircraft(option)
      return
    }
    onSetAircraft(day, option)
    setActiveAircraftId(option.fleetAircraftId)
    setStep('leg')
  }

  function confirmAircraftChange() {
    if (!pendingAircraft) return
    onSetAircraft(day, pendingAircraft)
    setActiveAircraftId(pendingAircraft.fleetAircraftId)
    setStep('leg')
    setPendingAircraft(null)
  }

  function handlePickLeg(option: LegalLegOption) {
    const blockMinutes = blockMinutesByRouteId[option.routeId] ?? 60
    const leg = draftLegFromOption(option, time, blockMinutes)
    onConfirmAdd(day, leg)
    onOpenChange(false)
  }

  function handleConfirmRetime() {
    if (!editingLeg) return
    const dayLegs = currentDay?.legs ?? []
    const candidate = { departureTimeUtc: `${time}:00`, blockMinutes: editingLeg.blockMinutes }
    const overlap = findOverlappingLeg(candidate, dayLegs, editingLeg.id)
    if (overlap) {
      setMoveError(
        `That slot overlaps ${overlap.flightNumber ?? 'another leg'} (${overlap.departureIcao} → ${overlap.arrivalIcao}, ${minutesToTime(timeToMinutes(overlap.departureTimeUtc)).slice(0, 5)}). Pick a different time.`,
      )
      return
    }
    onConfirmRetime(day, editingLeg.id, `${time}:00`)
    onOpenChange(false)
  }

  const legalOptions = legOptions?.legal ?? []
  const illegalOptions = legOptions?.illegal ?? []

  const title =
    mode === 'edit'
      ? `Edit ${editingLeg?.flightNumber ?? 'leg'}`
      : step === 'aircraft'
        ? 'Choose an aircraft'
        : 'Add a leg'

  const description =
    mode === 'edit'
      ? 'Retime or remove this leg. The aircraft for this duty day is unchanged here.'
      : step === 'aircraft'
        ? "Pick the aircraft for this pilot's whole duty day - you'll choose legs for it next."
        : `Choose a departure time, then pick from what ${currentDay?.registration ?? 'this aircraft'} can actually fly in that slot.`

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>

        {step === 'leg' && (
          <div className="space-y-1.5">
            <Label htmlFor="leg-time">Departure (UTC)</Label>
            <input
              id="leg-time"
              type="time"
              value={time}
              onChange={(event) => setTime(event.target.value)}
              className="flex h-9 w-full rounded-md border border-input bg-surface px-3 py-1 text-sm shadow-elevation-1"
            />
          </div>
        )}

        {mode === 'add' && step === 'aircraft' && (
          <div className="max-h-96 space-y-3 overflow-y-auto">
            {pendingAircraft ? (
              <div className="space-y-3 rounded-md border border-warning/30 bg-warning/10 p-3">
                <p className="flex items-start gap-2 text-sm text-warning">
                  <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                  <span className="min-w-0 break-words">
                    This day already has {currentDay?.legs.length} leg{currentDay?.legs.length === 1 ? '' : 's'} scheduled on{' '}
                    {currentDay?.registration}. Switching to {pendingAircraft.registration} clears{' '}
                    {currentDay?.legs.length === 1 ? 'it' : 'them'} - you'll add legs again for the new aircraft.
                  </span>
                </p>
                <div className="flex justify-end gap-2">
                  <Button type="button" variant="outline" size="sm" onClick={() => setPendingAircraft(null)}>
                    Cancel
                  </Button>
                  <Button type="button" variant="destructive" size="sm" onClick={confirmAircraftChange}>
                    Switch aircraft
                  </Button>
                </div>
              </div>
            ) : (
              <AircraftPicker
                options={aircraftOptions?.options ?? []}
                status={aircraftStatus === 'idle' ? 'loading' : aircraftStatus}
                selectedAircraftId={activeAircraftId}
                onSelect={handlePickAircraft}
                onNavigateAway={() => onOpenChange(false)}
              />
            )}
          </div>
        )}

        {mode === 'add' && step === 'leg' && (
          <div className="max-h-72 space-y-2 overflow-y-auto">
            {legStatus === 'loading' && (
              <div className="space-y-2">
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
              </div>
            )}

            {legStatus === 'error' && <p className="text-sm text-danger">Could not load legs for this slot. Try again.</p>}

            {legStatus === 'ready' && legalOptions.length === 0 && illegalOptions.length === 0 && (
              <p className="rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground">
                No routes yet - create one on the Routes page first.
              </p>
            )}

            {legStatus === 'ready' &&
              legalOptions.map((option) => {
                const blockMinutes = blockMinutesByRouteId[option.routeId]
                return (
                  <LegOptionRow key={option.routeId} option={option} blockMinutes={blockMinutes} onPick={() => handlePickLeg(option)} />
                )
              })}

            {legStatus === 'ready' && illegalOptions.length > 0 && (
              <div className="space-y-1">
                {legalOptions.length > 0 && (
                  <p className="pt-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">Not available in this slot</p>
                )}
                {illegalOptions.map(({ routeId, reason }) => (
                  <div key={routeId} className="flex items-start gap-2 rounded-md border border-dashed border-border p-2 text-sm text-muted-foreground">
                    <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
                    <span className="min-w-0 break-words">{reason}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

        {mode === 'edit' && moveError && (
          <p className="flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 p-2.5 text-sm text-danger">
            <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
            <span className="min-w-0 break-words">{moveError}</span>
          </p>
        )}

        <DialogFooter>
          {mode === 'edit' && editingLeg && (
            <Button
              type="button"
              variant="destructive"
              className="sm:mr-auto"
              onClick={() => {
                onRemove(day, editingLeg.id)
                onOpenChange(false)
              }}
            >
              Remove leg
            </Button>
          )}
          {mode === 'add' && step === 'leg' && (
            <Button type="button" variant="outline" className="sm:mr-auto" onClick={() => setStep('aircraft')}>
              Change aircraft
            </Button>
          )}
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          {mode === 'edit' && (
            <Button type="button" onClick={handleConfirmRetime}>
              Save time
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function LegOptionRow({ option, blockMinutes, onPick }: { option: LegalLegOption; blockMinutes: number | undefined; onPick: () => void }) {
  const { fmt } = useSettings()
  return (
    <button
      type="button"
      onClick={onPick}
      className={cn(
        'flex w-full items-center justify-between gap-3 rounded-md border border-border p-2.5 text-left text-sm transition-colors hover:border-ring hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
      )}
    >
      <span className="flex min-w-0 items-center gap-2">
        <Plane className="size-4 shrink-0 text-accent" aria-hidden="true" />
        <span className="min-w-0 break-words">
          <span className="font-mono font-medium">
            {option.departureIcao} → {option.arrivalIcao}
          </span>
          {option.flightNumber && <span className="ml-2 text-muted-foreground">{option.flightNumber}</span>}
          {blockMinutes != null && <span className="ml-2 text-muted-foreground">{fmt.duration(blockMinutes)}</span>}
        </span>
      </span>
    </button>
  )
}
