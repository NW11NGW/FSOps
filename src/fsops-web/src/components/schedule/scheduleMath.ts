import type { DraftEntry } from './draftEntry'
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

/** One rendered block for an entry that departs on this day - top/height are pixel offsets
 *  within the day column. `overflowMinutes` is how much of the leg spills past midnight into the
 *  next day's column (0 for the common case of a leg that lands the same day it departs). */
export interface PositionedBlock {
  entry: DraftEntry
  top: number
  height: number
  overflowMinutes: number
  /** True if this block's time range overlaps another entry on the same day - rendered as a
   *  clear, unmissable conflict rather than one block silently hiding behind another. */
  overlapping: boolean
}

/** A continuation strip at the top of a day column for a leg that departed the previous day and
 *  is still "in the air" (gate-to-gate) past midnight. */
export interface SpilloverBlock {
  entry: DraftEntry
  height: number
}

/** The gap between two consecutive legs on the same day, purely for the "is this day
 *  over-stuffed" visual - see TIGHT_TURNAROUND_MINUTES. */
export interface TurnaroundGap {
  top: number
  height: number
  minutes: number
  tight: boolean
}

export interface DayLayout {
  blocks: PositionedBlock[]
  spillovers: SpilloverBlock[]
  gaps: TurnaroundGap[]
}

/** Lays out every entry departing on `day`, plus any spillover from `day - 1`, into pixel
 *  positions the grid can render directly. Pure and side-effect free so it can run on every
 *  render without memoisation worries at the entry counts this feature deals with. */
export function layoutDay(day: DayOfWeek, allEntries: DraftEntry[]): DayLayout {
  const previousDay = ((day + 6) % 7) as DayOfWeek
  const todays = allEntries
    .filter((e) => e.dayOfWeek === day)
    .sort((a, b) => timeToMinutes(a.departureTimeUtc) - timeToMinutes(b.departureTimeUtc))
  const previousDays = allEntries.filter((e) => e.dayOfWeek === previousDay)

  const intervals = todays.map((entry) => {
    const start = timeToMinutes(entry.departureTimeUtc)
    return { entry, start, end: start + entry.blockMinutes }
  })

  const blocks: PositionedBlock[] = intervals.map(({ entry, start, end }) => {
    const overlapping = intervals.some(
      (other) => other.entry.id !== entry.id && start < other.end && other.start < end,
    )
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

  const spillovers: SpilloverBlock[] = previousDays.flatMap((entry) => {
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

  return { blocks, spillovers, gaps }
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
