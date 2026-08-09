/** Pixels per minute for the calendar's vertical time axis. 1440 minutes/day * 1px = a tall but
 *  precise, scrollable day column - fine detail (5-minute drag snapping) stays legible at this
 *  scale without the grid becoming unreadably short. */
export const PX_PER_MIN = 1

/** Floor on a rendered block's height so a very short sector (e.g. a 25-minute hop) stays a
 *  clickable, readable target rather than a sliver. */
export const MIN_BLOCK_PX = 34

/** Below this many minutes of gap between two consecutive legs on the same duty day (always the
 *  same airframe - a day carries exactly one aircraft, docs/PLAN.md "2a"), the grid renders the
 *  gap as a visibly tight "turnaround" band rather than plain empty space. This is a display hint
 *  only - the backend's own minimum-turnaround rule is authoritative and surfaces as a save
 *  conflict if violated. */
export const TIGHT_TURNAROUND_MINUTES = 45
