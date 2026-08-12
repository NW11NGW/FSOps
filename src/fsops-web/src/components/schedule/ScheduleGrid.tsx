import { useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from 'react'
import { Plane, Plus, Repeat, TriangleAlert } from 'lucide-react'

import type { DraftLeg, DraftWeek } from './draftEntry'
import { layoutDay, minuteToHHMM, pixelsToSnappedMinute } from './scheduleMath'
import { MIN_BLOCK_PX, PX_PER_MIN } from './types'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { DAY_DISPLAY_ORDER, DAY_LABELS, DAY_SHORT_LABELS, type DayOfWeek } from '@/types/schedule'

export const ENTRY_DRAG_TYPE = 'text/x-fsops-leg-id'

const HOURS = Array.from({ length: 24 }, (_, i) => i)
const DAY_HEIGHT = 24 * 60 * PX_PER_MIN
const CORNER_WIDTH = 56

interface ScheduleGridProps {
  week: DraftWeek
  onAddClick: (day: DayOfWeek, time?: string) => void
  onChangeAircraftClick: (day: DayOfWeek) => void
  onMoveLeg: (day: DayOfWeek, legId: string, time: string) => void
  onSelectLeg: (day: DayOfWeek, leg: DraftLeg) => void
}

/** Finds which day currently holds a leg id - used to reject a drag-move that was dropped onto a
 *  different day's column. A leg's aircraft belongs to its own duty day, so moving it across days
 *  would silently reassign it to a different airframe without going through the picker; instead of
 *  allowing that, cross-day drags are simply ignored: the aircraft is a duty-day decision, not a
 *  per-leg one. */
function findLegDay(week: DraftWeek, legId: string): DayOfWeek | null {
  for (const day of Object.values(week)) {
    if (day?.legs.some((l) => l.id === legId)) return day.dayOfWeek
  }
  return null
}

/**
 * The weekly timetable: time runs top-to-bottom, days run left-to-right, like a diary. A leg
 * occupies a block sized to its real gate-to-gate duration (see scheduleMath.layoutDay), so a
 * packed day is visibly packed. Each day column carries at most one aircraft -
 * its registration sits in the column header, never repeated per leg, and turnaround gaps between
 * legs are always that same airframe's own gap. The week repeats indefinitely - there is no "last
 * week of the month" concept, which is the point: set once, flies forever until changed.
 */
export function ScheduleGrid({ week, onAddClick, onChangeAircraftClick, onMoveLeg, onSelectLeg }: ScheduleGridProps) {
  const columnRefs = useRef<Map<DayOfWeek, HTMLDivElement>>(new Map())
  const [dragHover, setDragHover] = useState<{ day: DayOfWeek; minute: number } | null>(null)

  function minuteFromEvent(day: DayOfWeek, clientY: number): number {
    const column = columnRefs.current.get(day)
    if (!column) return 0
    const rect = column.getBoundingClientRect()
    return pixelsToSnappedMinute(clientY - rect.top)
  }

  function handleDragOver(event: DragEvent<HTMLDivElement>, day: DayOfWeek) {
    if (!event.dataTransfer.types.includes(ENTRY_DRAG_TYPE)) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
    setDragHover({ day, minute: minuteFromEvent(day, event.clientY) })
  }

  function handleDrop(event: DragEvent<HTMLDivElement>, day: DayOfWeek) {
    const legId = event.dataTransfer.getData(ENTRY_DRAG_TYPE)
    setDragHover(null)
    if (!legId) return
    event.preventDefault()
    if (findLegDay(week, legId) !== day) return // a leg only ever moves within its own day's column
    const minute = minuteFromEvent(day, event.clientY)
    onMoveLeg(day, legId, `${minuteToHHMM(minute)}:00`)
  }

  function handleColumnClick(event: MouseEvent<HTMLDivElement>, day: DayOfWeek) {
    if (event.target !== event.currentTarget) return // a block/gap handled its own click
    const minute = minuteFromEvent(day, event.clientY)
    onAddClick(day, minuteToHHMM(minute))
  }

  function handleBlockKeyDown(event: KeyboardEvent<HTMLDivElement>, day: DayOfWeek, entry: DraftLeg) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onSelectLeg(day, entry)
    }
  }

  return (
    <div className="relative max-h-[640px] overflow-auto rounded-lg border border-border">
      <div
        className="grid"
        style={{ gridTemplateColumns: `${CORNER_WIDTH}px repeat(7, minmax(160px, 1fr))`, minWidth: 980 }}
      >
        {/* Header row - each day's aircraft (or a prompt to pick one) plus an add-leg button. */}
        <div className="sticky top-0 z-20 border-b border-border bg-surface-elevated" />
        {DAY_DISPLAY_ORDER.map((day) => {
          const { registration } = layoutDay(day, week)
          return (
            <div
              key={`head-${day}`}
              className="sticky top-0 z-20 space-y-1 border-b border-l border-border bg-surface-elevated px-2 py-2"
            >
              <div className="flex items-center justify-between gap-1">
                <span className="min-w-0 break-words text-sm font-semibold">{DAY_SHORT_LABELS[day]}</span>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="size-6 shrink-0 text-muted-foreground"
                  aria-label={`Add leg on ${DAY_LABELS[day]}`}
                  onClick={() => onAddClick(day)}
                >
                  <Plus className="size-3.5" />
                </Button>
              </div>
              {registration ? (
                <button
                  type="button"
                  onClick={() => onChangeAircraftClick(day)}
                  className="flex w-full min-w-0 items-center gap-1 rounded border border-border bg-surface px-1.5 py-0.5 text-left text-[11px] text-muted-foreground transition-colors hover:border-ring hover:text-foreground"
                  title={`Change the aircraft for ${DAY_LABELS[day]}`}
                >
                  <Plane className="size-3 shrink-0 text-accent" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate font-mono">{registration}</span>
                  <Repeat className="size-3 shrink-0" aria-hidden="true" />
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => onChangeAircraftClick(day)}
                  className="w-full min-w-0 truncate rounded border border-dashed border-border px-1.5 py-0.5 text-left text-[11px] text-muted-foreground transition-colors hover:border-ring hover:text-foreground"
                >
                  No aircraft yet
                </button>
              )}
            </div>
          )
        })}

        {/* Hour axis */}
        <div className="relative" style={{ height: DAY_HEIGHT }}>
          {HOURS.map((hour) => (
            <span
              key={hour}
              className="absolute right-1.5 -translate-y-1/2 text-[10px] tabular-nums text-muted-foreground"
              style={{ top: hour * 60 * PX_PER_MIN }}
            >
              {String(hour).padStart(2, '0')}:00
            </span>
          ))}
        </div>

        {/* Day columns */}
        {DAY_DISPLAY_ORDER.map((day) => {
          const { blocks, spillovers, gaps } = layoutDay(day, week)
          const showGhost = dragHover?.day === day
          const previousDay = ((day + 6) % 7) as DayOfWeek
          const previousDayLabel = DAY_LABELS[previousDay]

          return (
            <div
              key={day}
              ref={(el) => {
                if (el) columnRefs.current.set(day, el)
                else columnRefs.current.delete(day)
              }}
              className="relative cursor-pointer border-l border-border bg-background/40"
              style={{ height: DAY_HEIGHT }}
              onClick={(event) => handleColumnClick(event, day)}
              onDragOver={(event) => handleDragOver(event, day)}
              onDragLeave={() => setDragHover((current) => (current?.day === day ? null : current))}
              onDrop={(event) => handleDrop(event, day)}
            >
              {HOURS.map((hour) => (
                <div
                  key={hour}
                  className="pointer-events-none absolute inset-x-0 border-t border-border/40"
                  style={{ top: hour * 60 * PX_PER_MIN }}
                />
              ))}

              {showGhost && (
                <div
                  className="pointer-events-none absolute inset-x-0 z-10 border-t-2 border-dashed border-accent"
                  style={{ top: dragHover.minute * PX_PER_MIN }}
                >
                  <span className="absolute -top-2.5 left-1 rounded bg-accent px-1 text-[10px] font-medium text-accent-foreground">
                    {minuteToHHMM(dragHover.minute)}
                  </span>
                </div>
              )}

              {spillovers.map(({ entry, height }) => (
                <div
                  key={`spill-${entry.id}`}
                  className="pointer-events-none absolute inset-x-0.5 top-0 z-0 rounded-b-md border border-dashed border-accent/50 bg-accent/10 px-1.5 py-1 text-[11px] text-muted-foreground"
                  style={{ height }}
                  title={`Continues from ${previousDayLabel} - ${entry.flightNumber ?? entry.routeId}`}
                >
                  <span className="min-w-0 break-words">cont'd — {entry.flightNumber ?? `${entry.departureIcao}→${entry.arrivalIcao}`}</span>
                </div>
              ))}

              {gaps
                .filter((gap) => gap.tight && gap.height >= 10)
                .map((gap, index) => (
                  <div
                    key={`gap-${index}`}
                    className="pointer-events-none absolute inset-x-1 z-0 rounded border border-dashed border-warning/50 bg-warning/10"
                    style={{ top: gap.top, height: gap.height }}
                  >
                    {gap.height >= 20 && (
                      <span className="flex h-full items-center justify-center text-[10px] font-medium text-warning">
                        {gap.minutes}m turnaround
                      </span>
                    )}
                  </div>
                ))}

              {blocks.map(({ entry, top, height, overlapping }) => (
                <div
                  key={entry.id}
                  role="button"
                  tabIndex={0}
                  draggable
                  aria-label={`${entry.flightNumber ?? ''} ${entry.departureIcao} to ${entry.arrivalIcao}, departs ${entry.departureTimeUtc.slice(0, 5)} UTC${overlapping ? ', overlaps another leg' : ''}`}
                  onClick={(event) => {
                    event.stopPropagation()
                    onSelectLeg(day, entry)
                  }}
                  onKeyDown={(event) => handleBlockKeyDown(event, day, entry)}
                  onDragStart={(event) => {
                    event.dataTransfer.setData(ENTRY_DRAG_TYPE, entry.id)
                    event.dataTransfer.effectAllowed = 'move'
                  }}
                  className={cn(
                    'absolute inset-x-0.5 z-10 flex cursor-grab flex-col gap-0.5 overflow-hidden rounded-md border px-1.5 py-1 text-left text-[11px] shadow-elevation-1 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring active:cursor-grabbing',
                    overlapping
                      ? 'border-danger bg-danger/15 hover:bg-danger/20'
                      : entry.isNew
                        ? 'border-dashed border-accent bg-accent/10 hover:bg-accent/20'
                        : 'border-accent/40 bg-accent/15 hover:bg-accent/20',
                  )}
                  style={{ top, height, minHeight: MIN_BLOCK_PX }}
                >
                  <span className="flex items-center gap-1 font-medium">
                    {overlapping ? (
                      <TriangleAlert className="size-3 shrink-0 text-danger" aria-hidden="true" />
                    ) : (
                      <Plane className="size-3 shrink-0 text-accent" aria-hidden="true" />
                    )}
                    <span className="min-w-0 truncate">{entry.flightNumber ?? `${entry.departureIcao}${entry.arrivalIcao}`}</span>
                  </span>
                  <span className="min-w-0 truncate text-muted-foreground">
                    {entry.departureIcao} → {entry.arrivalIcao} · {entry.departureTimeUtc.slice(0, 5)}
                  </span>
                </div>
              ))}
            </div>
          )
        })}
      </div>
    </div>
  )
}
