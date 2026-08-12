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
