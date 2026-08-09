import type { DraftDay, DraftLeg, DraftWeek } from './draftEntry'
import { MIN_BLOCK_PX, PX_PER_MIN, TIGHT_TURNAROUND_MINUTES } from './types'
import { timeToMinutes } from '@/types/schedule'
import type { DayOfWeek } from '@/types/schedule'

const DAY_MINUTES = 1440

/** Pairs each element with its successor - avoids indexed access into `arr[i + 1]`, which
 *  TypeScript's noUncheckedIndexedAccess (rightly) treats as possibly undefined. */
function zipConsecutive<T>(items: T[]): [T, T][] {
  const pairs: [T, T][] = []
  for (let i = 1; i < items.length; i += 1) {
    const previous = items[i - 1]
    const current = items[i]
    if (previous !== undefined && current !== undefined) pairs.push([previous, current])
  }
  return pairs
}

/** One rendered block for a leg that departs on this day - top/height are pixel offsets within
 *  the day column. `overflowMinutes` is how much of the leg spills past midnight into the next
 *  day's column (0 for the common case of a leg that lands the same day it departs). */
export interface PositionedBlock {
  entry: DraftLeg
  top: number
  height: number
  overflowMinutes: number
  /** True if this block's time range overlaps another leg on the same duty day - rendered as a
   *  clear, unmissable conflict rather than one block silently hiding behind another. Since a
   *  duty day carries exactly one aircraft, any overlap here is a genuine double-booking, not an
   *  artifact of two different airframes sharing a column. */
  overlapping: boolean
}

/** A continuation strip at the top of a day column for a leg that departed the previous day and
 *  is still "in the air" (gate-to-gate) past midnight. */
export interface SpilloverBlock {
  entry: DraftLeg
  height: number
}

/** The gap between two consecutive legs on the SAME duty day - always the same airframe, since a
 *  day carries exactly one aircraft (docs/PLAN.md "2a"). Purely for the "is this day over-stuffed"
 *  visual - see TIGHT_TURNAROUND_MINUTES. */
export interface TurnaroundGap {
  top: number
  height: number
  minutes: number
  tight: boolean
}

export interface DayLayout {
  fleetAircraftId: string | null
  registration: string | null
  blocks: PositionedBlock[]
  spillovers: SpilloverBlock[]
  gaps: TurnaroundGap[]
}

/** Lays out one day's legs, plus any spillover from the day before, into pixel positions the grid
 *  can render directly. The turnaround gaps are computed purely from `day`'s own legs, which are
 *  always the same airframe - there is no cross-aircraft gap to guard against. Pure and
 *  side-effect free so it can run on every render without memoisation worries at the leg counts
 *  this feature deals with. */
export function layoutDay(day: DayOfWeek, week: DraftWeek): DayLayout {
  const previousDay = ((day + 6) % 7) as DayOfWeek
  const today: DraftDay | undefined = week[day]
  const previous: DraftDay | undefined = week[previousDay]

  const todaysLegs = today ? [...today.legs].sort((a, b) => timeToMinutes(a.departureTimeUtc) - timeToMinutes(b.departureTimeUtc)) : []

  const intervals = todaysLegs.map((entry) => {
    const start = timeToMinutes(entry.departureTimeUtc)
    return { entry, start, end: start + entry.blockMinutes }
  })

  const blocks: PositionedBlock[] = intervals.map(({ entry, start, end }) => {
    const overlapping = intervals.some((other) => other.entry.id !== entry.id && start < other.end && other.start < end)
    const clippedEnd = Math.min(end, DAY_MINUTES)
    const rawHeight = (clippedEnd - start) * PX_PER_MIN
    return {
      entry,
      top: start * PX_PER_MIN,
      height: Math.max(rawHeight, MIN_BLOCK_PX),
      overflowMinutes: Math.max(0, end - DAY_MINUTES),
      overlapping,
    }
  })

  const spillovers: SpilloverBlock[] = (previous?.legs ?? []).flatMap((entry) => {
    const start = timeToMinutes(entry.departureTimeUtc)
    const overflow = Math.max(0, start + entry.blockMinutes - DAY_MINUTES)
    if (overflow <= 0) return []
    return [{ entry, height: Math.max(overflow * PX_PER_MIN, MIN_BLOCK_PX / 2) }]
  })

  const gaps: TurnaroundGap[] = []
  for (const [current, next] of zipConsecutive(intervals)) {
    const gapMinutes = next.start - current.end
    if (gapMinutes <= 0) continue // overlap, not a gap - already flagged on the blocks themselves
    gaps.push({
      top: current.end * PX_PER_MIN,
      height: gapMinutes * PX_PER_MIN,
      minutes: gapMinutes,
      tight: gapMinutes < TIGHT_TURNAROUND_MINUTES,
    })
  }

  return {
    fleetAircraftId: today?.fleetAircraftId ?? null,
    registration: today?.registration ?? null,
    blocks,
    spillovers,
    gaps,
  }
}

/** Snaps a raw pixel offset within a day column to the nearest 5-minute mark, clamped to a valid
 *  minute-of-day - used to turn a drop/click Y coordinate into a departure time. */
export function pixelsToSnappedMinute(offsetY: number): number {
  const rawMinute = offsetY / PX_PER_MIN
  const snapped = Math.round(rawMinute / 5) * 5
  return Math.min(Math.max(snapped, 0), DAY_MINUTES - 5)
}

export function minuteToHHMM(minute: number): string {
  const hours = Math.floor(minute / 60)
  const mins = minute % 60
  return `${String(hours).padStart(2, '0')}:${String(mins).padStart(2, '0')}`
}
