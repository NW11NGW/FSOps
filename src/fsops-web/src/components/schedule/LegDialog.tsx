import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { ChevronDown, Plane, ShieldAlert, TriangleAlert } from 'lucide-react'

import { draftFromOption, draftToScheduleInputs, findOverlappingEntry, type DraftEntry } from './draftEntry'
import { groupOptionsByAircraft, headlineReason, type AircraftOptionGroup } from './optionGroups'
import type { RouteLike } from './types'
import { Badge } from '@/components/ui/badge'
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
import { fetchScheduleOptions } from '@/hooks/useSchedule'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import {
  DAY_DISPLAY_ORDER,
  DAY_LABELS,
  minutesToTime,
  timeToMinutes,
  type DayOfWeek,
  type FleetAircraftLite,
  type LegalScheduleOption,
  type ScheduleOptionsResponse,
} from '@/types/schedule'

interface LegDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  pilotId: string
  /** "add" fetches legal/illegal combinations and lets the player pick one. "edit" only lets the
   *  player retime or remove the leg it already refers to - its route/aircraft don't change. */
  mode: 'add' | 'edit'
  initialDay: DayOfWeek
  /** "HH:mm" */
  initialTime: string
  /** Pre-highlights this route when opened by dropping a palette chip onto the grid. */
  initialRouteId?: string
  editingEntry?: DraftEntry
  routes: RouteLike[]
  fleetById: Map<string, FleetAircraftLite>
  draft: DraftEntry[]
  onConfirmAdd: (entry: DraftEntry) => void
  onConfirmMove: (entryId: string, day: DayOfWeek, time: string) => void
  onRemove: (entryId: string) => void
}

/** The keyboard- and mouse-accessible way to place a leg: pick a day and time, then choose from
 *  the backend's own legal/illegal breakdown for that exact slot. Dragging a route chip onto the
 *  grid opens this same dialog pre-filled - there is no separate "blind drop" path, so illegal
 *  combinations are always shown with their reason rather than silently rejected. */
export function LegDialog({
  open,
  onOpenChange,
  pilotId,
  mode,
  initialDay,
  initialTime,
  initialRouteId,
  editingEntry,
  routes,
  fleetById,
  draft,
  onConfirmAdd,
  onConfirmMove,
  onRemove,
}: LegDialogProps) {
  const [day, setDay] = useState<DayOfWeek>(initialDay)
  const [time, setTime] = useState(initialTime)
  const [options, setOptions] = useState<ScheduleOptionsResponse | null>(null)
  const [optionsStatus, setOptionsStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')
  const [moveError, setMoveError] = useState<string | null>(null)
  /** Which airframe is expanded below the aircraft row - the whole point of picking the aircraft
   *  first (docs/PLAN.md "2b"): one aircraft's reasons at a time, not a route x aircraft grid. */
  const [selectedAircraftId, setSelectedAircraftId] = useState<string | null>(null)

  const debouncedTime = useDebouncedValue(time, 250)

  useEffect(() => {
    if (!open) return
    setDay(initialDay)
    setTime(initialTime)
    setMoveError(null)
    setSelectedAircraftId(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initialDay, initialTime, editingEntry?.id])

  useEffect(() => {
    if (!open || mode !== 'add') return
    if (!/^\d{2}:\d{2}$/.test(debouncedTime)) return
    let cancelled = false
    setOptionsStatus('loading')
    // The current on-screen draft (saved or not) is sent so the backend judges this candidate
    // against what's actually been built so far - week-closure isn't required here, only at save,
    // which is what makes it possible to add legs one at a time starting from an empty week.
    fetchScheduleOptions(pilotId, day, debouncedTime, draftToScheduleInputs(draft))
      .then((result) => {
        if (cancelled) return
        setOptions(result)
        setOptionsStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setOptionsStatus('error')
      })
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, mode, pilotId, day, debouncedTime, draft])

  const routeById = useMemo(() => new Map(routes.map((r) => [r.id, r])), [routes])

  const aircraftGroups = useMemo(
    () => (options ? groupOptionsByAircraft(options, fleetById) : []),
    [options, fleetById],
  )

  // Keep the selection valid as options reload for a new day/time, and make sure something is
  // always expanded rather than opening on a blank "choose an aircraft" state - preferring
  // whichever aircraft can actually fly the route that was dropped onto the grid (if any), then
  // any aircraft with something available, then just the first group.
  useEffect(() => {
    // Capture the fallback OUTSIDE the state callback: the `length === 0` guard cannot narrow
    // `aircraftGroups[0]` inside the closure under the build's stricter index checking, and an
    // empty list is already handled by the early return below.
    const [firstGroup] = aircraftGroups
    if (!firstGroup) {
      setSelectedAircraftId(null)
      return
    }
    setSelectedAircraftId((current) => {
      if (current && aircraftGroups.some((g) => g.fleetAircraftId === current)) return current
      const forInitialRoute = initialRouteId
        ? aircraftGroups.find((g) => g.legal.some((o) => o.routeId === initialRouteId))
        : undefined
      const firstAvailable = aircraftGroups.find((g) => g.legal.length > 0)
      return (forInitialRoute ?? firstAvailable ?? firstGroup).fleetAircraftId
    })
  }, [aircraftGroups, initialRouteId])

  function handlePickOption(option: LegalScheduleOption) {
    const entry = draftFromOption(option, day, time, routeById.get(option.routeId))
    onConfirmAdd(entry)
    onOpenChange(false)
  }

  function handleConfirmMove() {
    if (!editingEntry) return
    const candidate = { dayOfWeek: day, departureTimeUtc: `${time}:00`, blockMinutes: editingEntry.blockMinutes }
    const overlap = findOverlappingEntry(candidate, draft, editingEntry.id)
    if (overlap) {
      setMoveError(
        `That slot overlaps ${overlap.flightNumber ?? 'another leg'} (${overlap.departureIcao} → ${overlap.arrivalIcao}, ${minutesToTime(timeToMinutes(overlap.departureTimeUtc)).slice(0, 5)}). Pick a different time or day.`,
      )
      return
    }
    onConfirmMove(editingEntry.id, day, `${time}:00`)
    onOpenChange(false)
  }

  const title = mode === 'add' ? 'Add a leg' : `Edit ${editingEntry?.flightNumber ?? 'leg'}`

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            {mode === 'add'
              ? 'Choose a day and departure time, then pick from what is actually flyable in that slot.'
              : 'Retime or remove this leg. Changing the route or aircraft is not possible here - remove it and add a new one instead.'}
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="leg-day">Day</Label>
            <select
              id="leg-day"
              value={day}
              onChange={(event) => setDay(Number(event.target.value) as DayOfWeek)}
              className="flex h-9 w-full rounded-md border border-input bg-surface px-3 py-1 text-sm shadow-elevation-1"
            >
              {DAY_DISPLAY_ORDER.map((d) => (
                <option key={d} value={d}>
                  {DAY_LABELS[d]}
                </option>
              ))}
            </select>
          </div>
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
        </div>

        {mode === 'add' && (
          <div className="max-h-72 space-y-3 overflow-y-auto">
            {optionsStatus === 'loading' && (
              <div className="space-y-2">
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
              </div>
            )}

            {optionsStatus === 'error' && (
              <p className="text-sm text-danger">Could not load options for this slot. Try again.</p>
            )}

            {optionsStatus === 'ready' && options && (
              <>
                {aircraftGroups.length === 0 && (
                  <p className="rounded-md border border-dashed border-border p-3 text-sm text-muted-foreground">
                    Nothing can depart at this time - try another slot.
                  </p>
                )}

                {aircraftGroups.length > 0 && (
                  <div className="space-y-1.5">
                    <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      Aircraft - pick one to see what it can fly in this slot
                    </p>
                    <div className="space-y-1.5">
                      {aircraftGroups.map((group) => (
                        <AircraftOptionRow
                          key={group.fleetAircraftId}
                          group={group}
                          expanded={group.fleetAircraftId === selectedAircraftId}
                          highlightedRouteId={initialRouteId}
                          routeById={routeById}
                          onToggle={() =>
                            setSelectedAircraftId((current) => (current === group.fleetAircraftId ? null : group.fleetAircraftId))
                          }
                          onPickOption={handlePickOption}
                          onNavigateAway={() => onOpenChange(false)}
                        />
                      ))}
                    </div>
                  </div>
                )}
              </>
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
          {mode === 'edit' && editingEntry && (
            <Button
              type="button"
              variant="destructive"
              className="sm:mr-auto"
              onClick={() => {
                onRemove(editingEntry.id)
                onOpenChange(false)
              }}
            >
              Remove leg
            </Button>
          )}
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          {mode === 'edit' && (
            <Button type="button" onClick={handleConfirmMove}>
              Save time
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

interface AircraftOptionRowProps {
  group: AircraftOptionGroup
  expanded: boolean
  highlightedRouteId?: string
  routeById: Map<string, RouteLike>
  onToggle: () => void
  onPickOption: (option: LegalScheduleOption) => void
  /** Closes the dialog before the reserved-aircraft row's Fleet-page link navigates away. */
  onNavigateAway: () => void
}

/**
 * One aircraft's collapsed line - registration plus the single most relevant reason (or how many
 * legs it can fly) - that expands to full detail on click. This is the whole fix for the "wall of
 * text" bug: six route x aircraft paragraphs become one line per aircraft, with detail only a
 * click away for the one the player is actually looking at (docs/PLAN.md "2b").
 */
function AircraftOptionRow({
  group,
  expanded,
  highlightedRouteId,
  routeById,
  onToggle,
  onPickOption,
  onNavigateAway,
}: AircraftOptionRowProps) {
  const { fmt } = useSettings()
  const headline = group.reserved
    ? 'Reserved for you'
    : group.legal.length > 0
      ? `${group.legal.length} leg${group.legal.length === 1 ? '' : 's'} available`
      : (headlineReason(group) ?? 'Unavailable')

  return (
    <div className="overflow-hidden rounded-md border border-border">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        className={cn(
          'flex w-full items-start gap-2.5 p-2.5 text-left text-sm transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
          expanded && 'bg-muted/40',
        )}
      >
        <Plane className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden="true" />
        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-center gap-2">
            <span className="font-mono font-medium">{group.registration}</span>
            <Badge
              variant={group.reserved ? 'muted' : group.legal.length > 0 ? 'success' : 'warning'}
              className="shrink-0"
            >
              {group.reserved ? 'Reserved' : group.legal.length > 0 ? `${group.legal.length} available` : 'Blocked'}
            </Badge>
          </span>
          {!group.reserved && <p className="mt-0.5 min-w-0 break-words text-xs text-muted-foreground">{headline}</p>}
        </span>
        <ChevronDown
          className={cn('mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform', expanded && 'rotate-180')}
          aria-hidden="true"
        />
      </button>

      {expanded && (
        <div className="space-y-1.5 border-t border-border p-2.5">
          {group.reserved && (
            <p className="flex items-start gap-2 text-sm text-muted-foreground">
              <ShieldAlert className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
              <span className="min-w-0 break-words">
                {group.registration} is reserved for you to fly and is never offered to a virtual pilot.{' '}
                <Link
                  to="/fleet"
                  onClick={onNavigateAway}
                  className="font-medium text-accent underline-offset-2 hover:underline"
                >
                  Release it from the Fleet page
                </Link>{' '}
                if you'd like a pilot to fly it instead.
              </span>
            </p>
          )}

          {!group.reserved &&
            group.legal.map((option) => {
              const route = routeById.get(option.routeId)
              const highlighted = highlightedRouteId === option.routeId
              return (
                <button
                  key={option.routeId}
                  type="button"
                  onClick={() => onPickOption(option)}
                  className={cn(
                    'flex w-full items-center justify-between gap-3 rounded-md border p-2.5 text-left text-sm transition-colors hover:border-ring hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                    highlighted ? 'border-accent bg-accent/10' : 'border-border',
                  )}
                >
                  <span className="flex min-w-0 items-center gap-2">
                    <Plane className="size-4 shrink-0 text-accent" aria-hidden="true" />
                    <span className="min-w-0 break-words">
                      <span className="font-mono font-medium">
                        {option.departureIcao} → {option.arrivalIcao}
                      </span>
                      {route?.flightNumber && <span className="ml-2 text-muted-foreground">{route.flightNumber}</span>}
                      {route?.estimatedBlockMinutes != null && (
                        <span className="ml-2 text-muted-foreground">{fmt.duration(route.estimatedBlockMinutes)}</span>
                      )}
                    </span>
                  </span>
                </button>
              )
            })}

          {!group.reserved && group.illegal.length > 0 && (
            <div className="space-y-1">
              {group.legal.length > 0 && (
                <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Also unavailable</p>
              )}
              {group.illegal.map(({ routeId, reason }) => {
                const route = routeById.get(routeId)
                return (
                  <div
                    key={routeId}
                    className="flex items-start gap-2 rounded-md border border-dashed border-border p-2 text-sm text-muted-foreground"
                  >
                    <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
                    <span className="min-w-0 break-words">
                      {route && <span className="mr-1 font-mono">{route.departureIcao} → {route.arrivalIcao}</span>}
                      {reason}
                    </span>
                  </div>
                )
              })}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
