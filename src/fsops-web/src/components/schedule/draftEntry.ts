import { timeToMinutes } from '@/types/schedule'
import type { DayOfWeek, DutyDayInput, LegalLegOption, ScheduleDutyDay } from '@/types/schedule'

/**
 * The grid's working copy of one leg. A loaded leg and a not-yet-saved addition share this shape
 * so the grid never needs to special-case "real" vs "pending" blocks - only `isNew` (for styling)
 * and `blockMinutes`'s provenance differ. A brand-new leg's blockMinutes comes from the route's
 * own estimate (a real, backend-computed figure, just not yet resolved against this exact
 * aircraft) until the next successful save replaces it with the server's authoritative value.
 */
export interface DraftLeg {
  /** Server id for a loaded leg, or a client-generated `draft-*` id for a pending addition -
   *  either way, stable for the life of this editing session so React keys and drag handles work. */
  id: string
  departureTimeUtc: string
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  blockMinutes: number
  isNew: boolean
}

/**
 * The grid's working copy of one duty day: the ONE aircraft chosen for it, plus its legs in
 * arrival order. A day with `fleetAircraftId` set and `legs: []` is a real, meaningful state - the
 * player picked an aircraft but hasn't added a leg yet, since the picker is deliberately aircraft
 * first, then legs.
 */
export interface DraftDay {
  dayOfWeek: DayOfWeek
  fleetAircraftId: string
  registration: string
  legs: DraftLeg[]
}

/** The whole in-progress week - sparse, keyed by day. A day absent from this map has no aircraft
 *  chosen and nothing scheduled, which is different from a day present with `legs: []`. */
export type DraftWeek = Partial<Record<DayOfWeek, DraftDay>>

let draftCounter = 0
export function nextDraftId(): string {
  draftCounter += 1
  return `draft-${Date.now()}-${draftCounter}`
}

export function scheduleToDraftWeek(dutyDays: ScheduleDutyDay[]): DraftWeek {
  const week: DraftWeek = {}
  for (const day of dutyDays) {
    week[day.dayOfWeek] = {
      dayOfWeek: day.dayOfWeek,
      fleetAircraftId: day.fleetAircraftId,
      registration: day.registration ?? '—',
      legs: day.legs.map((leg) => ({
        id: leg.id,
        departureTimeUtc: leg.departureTimeUtc,
        routeId: leg.routeId,
        departureIcao: leg.departureIcao,
        arrivalIcao: leg.arrivalIcao,
        flightNumber: leg.flightNumber,
        blockMinutes: leg.blockMinutes ?? 60,
        isNew: false,
      })),
    }
  }
  return week
}

/** Only days with at least one leg are worth sending - PUT treats an omitted day exactly like an
 *  empty one, and a day with an aircraft chosen but no legs isn't schedulable yet anyway. Used for
 *  both PUT's `dutyDays` and leg-options' `draftDutyDays` - same shape, same "days under
 *  construction" semantics. */
export function draftWeekToInput(week: DraftWeek): DutyDayInput[] {
  return Object.values(week)
    .filter((day): day is DraftDay => Boolean(day) && day!.legs.length > 0)
    .map((day) => ({
      dayOfWeek: day.dayOfWeek,
      fleetAircraftId: day.fleetAircraftId,
      legs: day.legs.map((leg) => ({ departureTimeUtc: leg.departureTimeUtc, routeId: leg.routeId })),
    }))
}

/** Deterministic string for a whole week - used for the dirty check (draft vs last-saved). */
export function weekSignature(week: DraftWeek): string {
  return Object.values(week)
    .filter((day): day is DraftDay => Boolean(day))
    .map((day) => {
      const legs = day.legs
        .map((l) => `${l.departureTimeUtc}|${l.routeId}`)
        .sort()
        .join(',')
      return `${day.dayOfWeek}:${day.fleetAircraftId}:[${legs}]`
    })
    .sort()
    .join(';')
}

/** Sets (or replaces) the aircraft for a day. Callers are responsible for confirming with the
 *  player before calling this on a day that already has legs under a DIFFERENT aircraft - those
 *  legs are cleared here unconditionally because they were only ever valid for the old aircraft. */
export function setDayAircraft(week: DraftWeek, day: DayOfWeek, fleetAircraftId: string, registration: string): DraftWeek {
  const existing = week[day]
  return {
    ...week,
    [day]: {
      dayOfWeek: day,
      fleetAircraftId,
      registration,
      legs: existing && existing.fleetAircraftId === fleetAircraftId ? existing.legs : [],
    },
  }
}

/** Removes a day entirely (no aircraft, no legs) - used when the player clears a day's aircraft
 *  rather than replacing it. */
export function clearDay(week: DraftWeek, day: DayOfWeek): DraftWeek {
  const next = { ...week }
  delete next[day]
  return next
}

export function addLegToDay(week: DraftWeek, day: DayOfWeek, leg: DraftLeg): DraftWeek {
  const existing = week[day]
  if (!existing) return week
  return { ...week, [day]: { ...existing, legs: [...existing.legs, leg] } }
}

export function removeLegFromDay(week: DraftWeek, day: DayOfWeek, legId: string): DraftWeek {
  const existing = week[day]
  if (!existing) return week
  return { ...week, [day]: { ...existing, legs: existing.legs.filter((l) => l.id !== legId) } }
}

/** One leg left stranded by a removal elsewhere in the week - not the leg being removed itself. */
export interface OrphanedLeg {
  day: DayOfWeek
  leg: DraftLeg
  /** Where the aircraft will actually be when this leg is due to depart, once every leg earlier in
   *  the week (chronologically, on this same aircraft) that still connects has been accounted for -
   *  NOT necessarily `aircraftLocationIcao`, since legs before the point of breakage still fly
   *  normally and carry the aircraft along the way before the break is reached. */
  aircraftActuallyAt: string
}

const WEEK_MINUTES = 7 * 24 * 60

/** "HH:mm:ss" plus a day-of-week, as one number - same construction as `WeekCycle.AbsoluteMinute` on
 *  the backend (DayOfWeek's .NET numbering, 0 = Sunday, is shared end to end - see
 *  types/schedule.ts's own remark). A position in the week, NOT an ordering: see
 *  {@link cycleOriginMinute} for why the two are different things. */
function absoluteWeekMinute(day: DayOfWeek, departureTimeUtc: string): number {
  return day * 1440 + timeToMinutes(departureTimeUtc)
}

/** How far forward round the cycle `minute` is from `origin`, always in [0, WEEK_MINUTES) - mirrors
 *  `WeekCycle.MinutesFrom`. */
function minutesFromOrigin(origin: number, minute: number): number {
  return (((minute - origin) % WEEK_MINUTES) + WEEK_MINUTES) % WEEK_MINUTES
}

/**
 * Where an aircraft enters its own weekly loop - the client-side mirror of `WeekCycle.OriginMinute`,
 * and it has to stay a mirror: this file walks the same chain the backend validates, and the two
 * disagreeing about where the week starts is how the grid comes to show a consequence the server
 * never reports (or vice versa).
 *
 * A saved schedule is a rolling cycle, so it has no first day. Ordering it from Sunday midnight,
 * which is what .NET's DayOfWeek numbering does if you sort by it, invents one - and then the leg
 * that gets anchored to where the airframe physically stands is whichever happens to fall on Sunday
 * rather than the leg the aircraft can actually take. That is the 2026-08-20 defect: a pilot flying
 * Monday to Saturday out of EGGD, aircraft parked at EGPH, had Sunday treated as the start of the
 * pattern. Rules, in order, as the backend applies them: a leg departing from where the aircraft is
 * AND preceded by a break in the chain; failing that, any leg departing from where it is.
 *
 * <b>With no known position, the week is NOT rotated at all</b> - deliberately narrower than the
 * backend, because this helper answers a different question. The backend rotates to the chain's own
 * break because it only needs to know which single pair a half-built week may leave open. Here,
 * rotating to the break would make the break itself the trusted starting point and therefore report
 * nothing at all - and the whole promise of {@link findLegsOrphanedByRemoval} is that with the
 * position unknown it still catches every break further into the chain. No position means no basis
 * for saying where the aircraft joins, so the calendar order stands and the walk stays conservative.
 */
function cycleOriginMinute(
  ordered: readonly { day: DayOfWeek; leg: DraftLeg }[],
  aircraftLocationIcao?: string,
): number {
  const first = ordered[0]
  if (!first || !aircraftLocationIcao) return 0

  const minuteOf = (entry: { day: DayOfWeek; leg: DraftLeg }) => absoluteWeekMinute(entry.day, entry.leg.departureTimeUtc)
  const isBreak = (index: number) => {
    const previous = ordered[(index - 1 + ordered.length) % ordered.length]
    const current = ordered[index]
    return Boolean(previous && current) && !icaoEquals(previous!.leg.arrivalIcao, current!.leg.departureIcao)
  }
  const departsFromAircraft = (index: number) => {
    const current = ordered[index]
    return Boolean(current) && icaoEquals(current!.leg.departureIcao, aircraftLocationIcao)
  }

  for (let i = 0; i < ordered.length; i += 1) if (departsFromAircraft(i) && isBreak(i)) return minuteOf(ordered[i]!)
  for (let i = 0; i < ordered.length; i += 1) if (departsFromAircraft(i)) return minuteOf(ordered[i]!)
  return minuteOf(first)
}

/** ICAO comparison the same way the backend does it (OrdinalIgnoreCase) - every ICAO in this app is
 *  already upper-case by the time it reaches the draft, but a helper that assumed that and broke
 *  silently on the one place it wasn't would be a worse bug than the one this file exists to catch. */
function icaoEquals(a: string, b: string): boolean {
  return a.toUpperCase() === b.toUpperCase()
}

/**
 * What removing one leg does to every OTHER leg on the same aircraft this week - not just the leg
 * immediately next to it. Real-use defect, 2026-08-13: a player scheduled an out-and-back, deleted
 * only the outbound, and the return silently stayed in the draft grid looking scheduled, even
 * though the aircraft was never repositioned to fly it. The backend's save-time validator already
 * refuses this outright (PilotScheduleValidator's first-leg-location anchor, under
 * `requireWeekClosure: true`) - the database was never at risk - but the player only finds out
 * after clicking Save, with a conflict about "the first leg of the week" that reads as a strange
 * new error rather than "you just deleted the wrong half of a pair." This is the draft-time half of
 * that fix: surface the SAME consequence the moment the player asks to remove a leg, before Save is
 * ever clicked, so the choice - and its wording - can live next to the delete button instead.
 *
 * Deliberately returns data, not a message: what got orphaned and where the aircraft would actually
 * be, never a sentence. The wording belongs to whichever component wires this up
 * (ScheduleBuilder.tsx / LegDialog.tsx), not to this pure helper - and the rule already established
 * elsewhere in this feature applies here too: tell the player what will happen and let them choose,
 * never cascade the removal silently.
 *
 * <b>Simulates the whole chain, not just the adjacent pair.</b> A leg two or three positions after
 * the one being removed can be orphaned even though its OWN departure/arrival still lines up with
 * its immediate predecessor on paper - because that predecessor itself never actually got there.
 * This walks the aircraft's whole week in departure order (across every duty day it flies, not just
 * the day the removal happened on), tracking where the aircraft would genuinely be at each point
 * (starting from `aircraftLocationIcao`, when known), and reports every leg whose departure does not
 * match that real, carried-forward position - not just the leg immediately following the gap.
 * Mirrors how VirtualFlightResolverService actually behaves at wall-clock resolution time: a leg
 * that can't be flown is skipped and the aircraft stays exactly where it was, so every later leg is
 * checked against that SAME frozen position until - if ever - one happens to depart from it.
 *
 * <b>Scope.</b> This is a same-week, same-aircraft domino check only - it does not evaluate whether
 * the week closes on itself (the last leg connecting back to the first for next week), which is
 * what the backend's `requireWeekClosure: true` wrap check is for. That's a whole-week property with
 * its own good error message at save time; this helper is about the immediate, in-view consequence
 * of one delete, not a full re-validation of the draft.
 *
 * @param aircraftLocationIcao The aircraft's actual recorded position (`FleetAircraftLite.locationIcao`),
 * when known. Anchors the earliest entry left in the week for this aircraft - a remainder that
 * already departs from here is never reported, even for the immediate half of an out-and-back (the
 * aircraft may genuinely already be at the away airport, e.g. from an earlier week's real flying).
 * When omitted, the anchor is simply unknown: the first remaining entry is trusted by default (never
 * itself reported), and only breaks this removal causes further into the chain are still detected.
 */
export function findLegsOrphanedByRemoval(week: DraftWeek, day: DayOfWeek, legId: string, aircraftLocationIcao?: string): OrphanedLeg[] {
  const targetDay = week[day]
  const removedLeg = targetDay?.legs.find((l) => l.id === legId)
  if (!targetDay || !removedLeg) return []

  const fleetAircraftId = targetDay.fleetAircraftId

  // Every OTHER leg on the SAME aircraft, anywhere in the week - continuity is a whole-aircraft
  // property, not a single-day one (one aircraft can carry more than one duty day across the week).
  // Excludes the leg being removed and every day flown by a different aircraft.
  const remaining: { day: DayOfWeek; leg: DraftLeg }[] = []
  for (const candidateDay of Object.values(week)) {
    if (!candidateDay || candidateDay.fleetAircraftId !== fleetAircraftId) continue
    for (const candidateLeg of candidateDay.legs) {
      if (candidateDay.dayOfWeek === day && candidateLeg.id === legId) continue
      remaining.push({ day: candidateDay.dayOfWeek, leg: candidateLeg })
    }
  }
  remaining.sort((a, b) => absoluteWeekMinute(a.day, a.leg.departureTimeUtc) - absoluteWeekMinute(b.day, b.leg.departureTimeUtc))

  // Re-order the week to start where the aircraft actually joins the loop rather than at Sunday
  // midnight - see cycleOriginMinute. Without this, a week that includes a Sunday is walked from
  // Sunday whatever the airframe is doing, and every leg from there to wherever the aircraft really
  // is reads as orphaned.
  const origin = cycleOriginMinute(remaining, aircraftLocationIcao)
  remaining.sort(
    (a, b) =>
      minutesFromOrigin(origin, absoluteWeekMinute(a.day, a.leg.departureTimeUtc)) -
      minutesFromOrigin(origin, absoluteWeekMinute(b.day, b.leg.departureTimeUtc)),
  )

  const orphaned: OrphanedLeg[] = []
  let position = aircraftLocationIcao ?? remaining[0]?.leg.departureIcao ?? null

  for (const entry of remaining) {
    if (position !== null && !icaoEquals(entry.leg.departureIcao, position)) {
      orphaned.push({ day: entry.day, leg: entry.leg, aircraftActuallyAt: position })
      // Frozen, not advanced: a leg that can't fly never moves the aircraft, so the next entry is
      // checked against this SAME position - see this function's own doc for why that mirrors the
      // resolver rather than the backend validator's local pairwise check.
      continue
    }
    position = entry.leg.arrivalIcao
  }

  return orphaned
}

/**
 * Removes the requested leg AND every leg `findLegsOrphanedByRemoval` reported for that same
 * removal, in one step. Not a second policy layered on top of that helper - it is the "yes, do it"
 * half of the same decision: the player has already been shown, leg by leg, exactly what this
 * strands (see OrphanedLegsDialog for the UI that gets them to this point), so leaving those legs
 * behind once they confirm would just hand back a draft that is guaranteed to fail at save for a
 * reason they were already told - a delete they would have to come back and make by hand a moment
 * later, not a state worth defending. `orphans` is taken as given (the caller's own, already-shown
 * `findLegsOrphanedByRemoval` result) rather than recomputed here, so what gets removed is always
 * exactly what was described.
 *
 * Any day left with zero legs afterwards has its aircraft released too - same tidy-up a plain
 * single-leg removal already does, just applied to every day this touches instead of only the one
 * the requested leg happened to be on.
 */
export function removeLegAndOrphans(week: DraftWeek, day: DayOfWeek, legId: string, orphans: OrphanedLeg[]): DraftWeek {
  let next = removeLegFromDay(week, day, legId)
  for (const orphan of orphans) {
    next = removeLegFromDay(next, orphan.day, orphan.leg.id)
  }

  const touchedDays = new Set<DayOfWeek>([day, ...orphans.map((orphan) => orphan.day)])
  for (const touchedDay of touchedDays) {
    if ((next[touchedDay]?.legs.length ?? 0) === 0) {
      next = clearDay(next, touchedDay)
    }
  }

  return next
}

/**
 * Copies one duty day onto one or more other days - the whole day, not a leg at a time: the same
 * aircraft, the same routes, the same departure times.
 *
 * <b>The aircraft comes with the copy, because a duty day has exactly one.</b> That is the
 * scheduler's central invariant (see PilotScheduleValidator.ValidateDutyDayAircraftConsistency), so
 * a "copy the legs but keep the target's own aircraft" variant is not a feature this could offer -
 * it would produce a day whose legs were resolved against a different airframe's block times.
 *
 * <b>Replacement is total and deliberate.</b> A target day that already has legs loses all of them.
 * That is only safe because the caller is required to have shown the player exactly which days lose
 * how many legs first (see CopyDayDialog) - the same "say what will happen and let them choose" rule
 * OrphanedLegsDialog follows. Nothing here is saved: the result is a draft, so Discard changes still
 * puts it back.
 *
 * <b>It does not check legality, and must not.</b> A pasted day is frequently illegal - Monday's
 * chain out of EGGD only works on Wednesday if the aircraft is at EGGD on Wednesday morning, which
 * depends entirely on what Tuesday did. Working that out is the backend's job and only the backend's
 * (POST /schedule/preview runs the save path's own validator); a copy of those rules living here
 * would be a second answer free to drift from the real one.
 */
export function copyDayTo(week: DraftWeek, sourceDay: DayOfWeek, targetDays: readonly DayOfWeek[]): DraftWeek {
  const source = week[sourceDay]
  if (!source || source.legs.length === 0) return week

  const next = { ...week }
  for (const targetDay of targetDays) {
    if (targetDay === sourceDay) continue
    next[targetDay] = {
      dayOfWeek: targetDay,
      fleetAircraftId: source.fleetAircraftId,
      registration: source.registration,
      // Fresh ids: React keys, drag handles and the orphan walk all identify a leg by id, and two
      // days sharing one would make the grid treat them as the same block.
      legs: source.legs.map((leg) => ({ ...leg, id: nextDraftId(), isNew: true })),
    }
  }
  return next
}

export function updateLegTime(week: DraftWeek, day: DayOfWeek, legId: string, departureTimeUtc: string): DraftWeek {
  const existing = week[day]
  if (!existing) return week
  return {
    ...week,
    [day]: { ...existing, legs: existing.legs.map((l) => (l.id === legId ? { ...l, departureTimeUtc } : l)) },
  }
}

/** Builds a new draft leg from a chosen legal option. `blockMinutes` is the route's own estimate -
 *  the best figure available client-side until the next save resolves the server's authoritative
 *  per-leg value. */
export function draftLegFromOption(option: LegalLegOption, time: string, blockMinutes: number): DraftLeg {
  return {
    id: nextDraftId(),
    departureTimeUtc: time.length === 5 ? `${time}:00` : time,
    routeId: option.routeId,
    departureIcao: option.departureIcao,
    arrivalIcao: option.arrivalIcao,
    flightNumber: option.flightNumber,
    blockMinutes,
    isNew: true,
  }
}

/** True if [startA, startA+lenA) and [startB, startB+lenB) overlap, treating minute ranges as
 *  half-open so a leg landing exactly when another departs does not count as an overlap. */
function rangesOverlap(startA: number, lenA: number, startB: number, lenB: number): boolean {
  return startA < startB + lenB && startB < startA + lenA
}

/** Any other leg on the SAME duty day whose time range overlaps this one - a pilot cannot be on
 *  two legs at once, and since a day carries exactly one aircraft, only same-day legs can ever
 *  conflict this way. Checked client-side before the slower round trip to the backend's full
 *  conflict check. */
export function findOverlappingLeg(
  candidate: { departureTimeUtc: string; blockMinutes: number },
  dayLegs: DraftLeg[],
  excludeId?: string,
): DraftLeg | null {
  const start = timeToMinutes(candidate.departureTimeUtc)
  for (const leg of dayLegs) {
    if (leg.id === excludeId) continue
    const legStart = timeToMinutes(leg.departureTimeUtc)
    if (rangesOverlap(start, candidate.blockMinutes, legStart, leg.blockMinutes)) return leg
  }
  return null
}
