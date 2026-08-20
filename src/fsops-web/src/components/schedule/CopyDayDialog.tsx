import { useCallback, useEffect, useState } from 'react'
import { Check, Copy, Info, Loader2, TriangleAlert } from 'lucide-react'

import { copyDayTo, draftWeekToInput, type DraftWeek } from './draftEntry'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { previewSchedule, type SchedulePreviewResult } from '@/hooks/useSchedule'
import { cn } from '@/lib/utils'
import { DAY_DISPLAY_ORDER, DAY_LABELS, DAY_SHORT_LABELS, type DayOfWeek } from '@/types/schedule'

interface CopyDayDialogProps {
  /** The day being copied, or null when the dialog is closed. */
  sourceDay: DayOfWeek | null
  week: DraftWeek
  pilotId: string
  onClose: () => void
  onConfirm: (next: DraftWeek) => void
}

/**
 * "Copy this day onto those days" - the answer to the player's own words: "save the tedious effort
 * of building each day individually." Their pilots run eight and nine legs a day across seven days,
 * every one of them placed by hand through the picker, so this is not a nicety.
 *
 * <b>The care is all in what happens when the copy would not be legal, which is often.</b> A duty
 * day is a chain: Monday's legs out of EGGD only work on Wednesday if the aircraft is at EGGD on
 * Wednesday morning, and that depends entirely on what Tuesday did. Real weeks chain across airports
 * - Michael Scott's Monday ends at EGPH and his Tuesday starts there - so a paste breaking continuity,
 * rest, turnaround, week closure or another pilot's booking is the ordinary case, not the edge one.
 * Three ways to handle that, and only one of them is any good:
 *
 * <ul>
 *   <li><b>Refuse outright</b> - safe, and useless: it leaves the player doing by hand the exact work
 *   they asked to be spared.</li>
 *   <li><b>Paste and let Save fail later</b> - worse than refusing, because it moves the failure away
 *   from the action that caused it, which is the single defect shape this scheduler has spent the
 *   most time undoing.</li>
 *   <li><b>Show what the paste would do, and what breaks, before it is committed</b> - the same
 *   discipline as the fare workbench, and what this dialog does.</li>
 * </ul>
 *
 * <b>The reasons are the backend's own sentences, never re-worded here.</b> Every target selection
 * re-asks POST /pilots/{id}/schedule/preview, which runs the save path's own evaluation with the
 * persistence left off - so what this dialog shows is exactly what Save would say, naming the
 * specific obstacle ("G-WJQG lands at LFPG ... but its next leg departs EGGD") rather than refusing
 * generically. There is no second copy of any rule in this file.
 *
 * <b>Nothing is destroyed silently.</b> A target day that already has legs is listed with how many it
 * would lose, before anything happens, and the confirm button says "Replace" rather than "Copy" the
 * moment that is true. And because the copy lands in the draft rather than the database, Discard
 * changes still undoes all of it.
 */
export function CopyDayDialog({ sourceDay, week, pilotId, onClose, onConfirm }: CopyDayDialogProps) {
  const [targets, setTargets] = useState<DayOfWeek[]>([])
  const [preview, setPreview] = useState<SchedulePreviewResult | null>(null)
  const [checking, setChecking] = useState(false)

  const source = sourceDay === null ? undefined : week[sourceDay]
  const sourceLegCount = source?.legs.length ?? 0

  // A fresh selection every time the dialog opens on a new day - a stale set of targets from the
  // last copy would be the sort of thing a player only notices after it has overwritten a day.
  useEffect(() => {
    setTargets([])
    setPreview(null)
    setChecking(false)
  }, [sourceDay])

  const runPreview = useCallback(
    async (nextTargets: DayOfWeek[]) => {
      if (sourceDay === null || nextTargets.length === 0) {
        setPreview(null)
        return
      }
      setChecking(true)
      const proposed = copyDayTo(week, sourceDay, nextTargets)
      const result = await previewSchedule(pilotId, draftWeekToInput(proposed))
      setPreview(result)
      setChecking(false)
    },
    [pilotId, sourceDay, week],
  )

  function toggleTarget(day: DayOfWeek) {
    const next = targets.includes(day) ? targets.filter((d) => d !== day) : [...targets, day]
    setTargets(next)
    void runPreview(next)
  }

  const replacing = targets
    .map((day) => ({ day, legs: week[day]?.legs.length ?? 0 }))
    .filter((entry) => entry.legs > 0)

  const conflicts = preview?.conflicts ?? []
  const wouldBeLegal = preview?.reachable === true && preview.isValid
  const couldNotCheck = preview?.reachable === false
  const confirmLabel = replacing.length > 0 ? `Replace ${targets.length === 1 ? '1 day' : `${targets.length} days`}` : `Copy to ${targets.length === 1 ? '1 day' : `${targets.length} days`}`

  function handleConfirm() {
    if (sourceDay === null || targets.length === 0) return
    onConfirm(copyDayTo(week, sourceDay, targets))
  }

  return (
    <Dialog open={sourceDay !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Copy {sourceDay === null ? 'this day' : DAY_LABELS[sourceDay]} to other days</DialogTitle>
          <DialogDescription>
            {sourceLegCount === 0
              ? 'This day has no legs to copy yet - add one first.'
              : `Copies all ${sourceLegCount} leg${sourceLegCount === 1 ? '' : 's'} and ${source?.registration ?? 'the aircraft'} onto the days you pick, at the same departure times. A duty day flies one aircraft throughout, so the aircraft comes with it.`}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="flex flex-wrap gap-1.5" role="group" aria-label="Days to copy onto">
            {DAY_DISPLAY_ORDER.map((day) => {
              const isSource = day === sourceDay
              const selected = targets.includes(day)
              const existingLegs = week[day]?.legs.length ?? 0
              return (
                <button
                  key={day}
                  type="button"
                  disabled={isSource || sourceLegCount === 0}
                  aria-pressed={selected}
                  onClick={() => toggleTarget(day)}
                  title={
                    isSource
                      ? `${DAY_LABELS[day]} is the day being copied`
                      : existingLegs > 0
                        ? `${DAY_LABELS[day]} already has ${existingLegs} leg${existingLegs === 1 ? '' : 's'} - copying replaces them`
                        : `Copy onto ${DAY_LABELS[day]}`
                  }
                  className={cn(
                    'flex min-w-[3.25rem] items-center justify-center gap-1 rounded-md border px-2 py-1.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50',
                    selected
                      ? 'border-accent bg-accent/20 text-foreground'
                      : 'border-border bg-surface text-muted-foreground hover:border-ring hover:text-foreground',
                  )}
                >
                  {selected && <Check className="size-3 shrink-0" aria-hidden="true" />}
                  {DAY_SHORT_LABELS[day]}
                </button>
              )
            })}
          </div>

          {replacing.length > 0 && (
            <p
              data-testid="copy-day-replace-warning"
              className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-2.5 text-sm text-warning"
            >
              <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">
                This replaces what is already there:{' '}
                {replacing.map((entry) => `${DAY_LABELS[entry.day]} (${entry.legs} leg${entry.legs === 1 ? '' : 's'})`).join(', ')}. Discard
                changes puts it back if you change your mind.
              </span>
            </p>
          )}

          {checking && (
            <p className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="size-4 animate-spin" aria-hidden="true" />
              Checking what this would do…
            </p>
          )}

          {!checking && couldNotCheck && (
            <p className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-2.5 text-sm text-warning">
              <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">
                Could not check this copy against your week - the server did not answer. You can still copy, but nothing has been checked, so
                save will have the last word.
              </span>
            </p>
          )}

          {!checking && wouldBeLegal && (
            <p
              data-testid="copy-day-clean"
              className="flex items-start gap-2 rounded-md border border-success/30 bg-success/10 p-2.5 text-sm text-success"
            >
              <Check className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 break-words">This copy works - the week still holds together with it.</span>
            </p>
          )}

          {!checking && conflicts.length > 0 && (
            <div data-testid="copy-day-conflicts" className="space-y-2">
              <p className="text-sm font-medium text-danger">
                {conflicts.length === 1 ? 'This copy breaks one thing:' : `This copy breaks ${conflicts.length} things:`}
              </p>
              <ul className="space-y-1.5">
                {conflicts.map((conflict, index) => (
                  <li
                    key={index}
                    className="flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 p-2.5 text-sm text-danger"
                  >
                    <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                    <span className="min-w-0 break-words">{conflict}</span>
                  </li>
                ))}
              </ul>
              <p className="text-xs text-muted-foreground">
                You can copy anyway and fix the days in between afterwards - but the week will not save until every one of these is resolved.
              </p>
            </div>
          )}
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            type="button"
            // Never the reassuring primary shape while something is known to be broken - the player
            // is choosing to take on work they have just been shown, and the button should look like
            // that choice rather than like the happy path.
            variant={conflicts.length > 0 ? 'outline' : 'default'}
            disabled={targets.length === 0 || sourceLegCount === 0 || checking}
            onClick={handleConfirm}
          >
            <Copy className="size-3.5" />
            {conflicts.length > 0 ? 'Copy anyway' : confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
