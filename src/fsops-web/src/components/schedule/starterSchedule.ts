import {
  addLegToDay,
  draftLegFromOption,
  draftWeekToInput,
  setDayAircraft,
  type DraftWeek,
} from './draftEntry'
import type { AircraftOptionsResponse, DayOfWeek, DutyDayInput, LegalLegOption, LegOptionsResponse } from '@/types/schedule'
import { minutesToTime, timeToMinutes } from '@/types/schedule'

/**
 * The "Suggest a starter schedule" generator - pure orchestration logic, deliberately separated
 * from ScheduleBuilder so it can be driven by fake `deps` in tests without a real server or a
 * mounted component. Every leg it proposes is checked against the SAME leg-options endpoint the
 * manual picker uses (see PilotEndpoints.GetLegOptionsAsync), so nothing here re-implements or
 * relaxes the real duty/rest/continuity/range/runway rules - it only ever accepts what the backend
 * already calls legal, which is what keeps the result always immediately saveable.
 *
 * Weekday mornings, same as before: every chain this generator proposes starts at {@link
 * STARTER_TIME} on one of {@link STARTER_DAYS}. What changed is what happens after that first
 * departure.
 *
 * <b>At most four legs a day - a ceiling on the GENERATOR only.</b> {@link MAX_LEGS_PER_GENERATED_DAY}
 * caps how many legs one calendar day can be offered here (two return trips); hand-building
 * through the picker can still go further, that is a different path this module has no say over.
 * Within that cap, real block time decides how many legs actually fit - a short sector can close
 * two round trips in a 13-hour duty day, a long-haul sector cannot even close one, and the
 * generator asks the real rules at every step rather than assuming either shape.
 *
 * <b>Block time is real, not a guess.</b> Every candidate's `blockMinutes` comes back from
 * leg-options resolved against the actual aircraft chosen for the day (see that endpoint's own
 * "K34" remarks) - the same figure a save would resolve to. The generator adds it, plus a
 * turnaround buffer, to place the NEXT candidate's departure, and only ever proposes that next
 * departure if it still lands on the same calendar day; a departure that would spill past midnight
 * is never sent to the backend; the day's own duty-hour and rest checks are what genuinely decide
 * whether a leg fits, this is just what keeps the times physically sane before asking.
 *
 * <b>A sector that cannot close same-day still gets a legal answer, not silence.</b> When the
 * outbound's own block time rules out a same-day return, the generator does not give up on the day
 * - it looks for a legal return on the VERY NEXT day instead (still within {@link STARTER_DAYS}),
 * which is what "one out-and-back across two days" means for a long-haul pair. If no legal return
 * exists there either, the whole chain is abandoned and NEITHER day is touched - a dangling
 * one-way leg would fail week-closure at save, so one is never proposed.
 */

/** Weekdays this generator ever proposes legs on - Monday through Friday. Unlike the manual
 *  picker, the starter schedule never touches the weekend. */
export const STARTER_DAYS: DayOfWeek[] = [1, 2, 3, 4, 5]

/** Every chain's first departure. Not a promise every leg lands in the morning - a day's second
 *  round trip, or a long-haul chain's return leg, departs whenever the real rules put it. */
export const STARTER_TIME = '08:00'

/** Gap left between one proposed leg's arrival and the next proposed leg's departure - just a
 *  starting guess, safely above `SchedulingConfig.MinTurnaroundMinutes` (30) but not tied to it.
 *  If it turns out tighter than what a specific slot actually needs, leg-options simply won't
 *  offer a legal next leg at that time and the generator backs off (same-day: stop at the round
 *  trip already closed; long-haul: try the next day) rather than proposing something that would
 *  fail at save. */
const LEG_GAP_MINUTES = 45

/** Deliberate ceiling on the generator only (see this module's own doc) - two return trips. */
const MAX_LEGS_PER_GENERATED_DAY = 4

const MINUTES_PER_DAY = 24 * 60

/** Everything the generator needs from the network, injected so it can be driven by canned
 *  responses in tests. Mirrors `fetchAircraftOptions`/`fetchLegOptions` from `useSchedule.ts`
 *  minus the pilot id, which the caller already has bound in. */
export interface StarterScheduleDeps {
  fetchAircraftOptions: (day: DayOfWeek) => Promise<AircraftOptionsResponse>
  fetchLegOptions: (
    day: DayOfWeek,
    time: string,
    fleetAircraftId: string,
    draftDutyDays: DutyDayInput[] | null,
  ) => Promise<LegOptionsResponse>
}

/**
 * Every precondition the generator can rule out BEFORE it tries to build anything, plus the one
 * genuine "the constraints don't line up" case. Each of the first four is knowable from a single
 * lookup and names its own fix; `no-legal-schedule` is the only one left generic, because it is
 * the only one where there genuinely isn't a single page that fixes it - see ScheduleBuilder.tsx
 * for the copy each of these renders as.
 */
export type StarterScheduleIssue =
  | { kind: 'no-routes' }
  | { kind: 'no-aircraft' }
  | { kind: 'all-reserved' }
  | { kind: 'no-usable-aircraft' }
  | { kind: 'no-legal-schedule' }
  | { kind: 'check-failed' }

export interface StarterScheduleBuilt {
  week: DraftWeek
  legsAdded: number
  daysUsed: number
}

export type StarterScheduleOutcome = { ok: true; result: StarterScheduleBuilt } | { ok: false; issue: StarterScheduleIssue }

/**
 * Builds the starter week. `routeCount` is passed in rather than fetched here - the caller
 * (ScheduleBuilder) already has the routes query loaded, and re-fetching it here would just be a
 * second source of truth for the same "do routes exist at all" fact.
 */
export async function buildStarterSchedule(routeCount: number, deps: StarterScheduleDeps): Promise<StarterScheduleOutcome> {
  if (routeCount === 0) {
    return { ok: false, issue: { kind: 'no-routes' } }
  }

  let aircraftOptions: AircraftOptionsResponse
  try {
    // Day-invariant (see PilotEndpoints.GetAircraftOptionsAsync - eligibility never actually
    // depends on which day is asked), so one call up front covers every chain this run builds
    // rather than re-asking per day the way the old single-day generator did.
    aircraftOptions = await deps.fetchAircraftOptions(STARTER_DAYS[0] as DayOfWeek)
  } catch {
    return { ok: false, issue: { kind: 'check-failed' } }
  }

  if (aircraftOptions.options.length === 0) {
    return { ok: false, issue: { kind: 'no-aircraft' } }
  }

  const eligible = aircraftOptions.options.filter((option) => option.eligible)
  if (eligible.length === 0) {
    // Reservation is the ONE ineligibility reason worth naming on its own (see this module's own
    // doc and the PART 1 brief this implements) - a fleet that is entirely reserved-to-the-player
    // is a deliberate choice the player made elsewhere, not an obviously broken state, so it reads
    // as confusing rather than informative unless it is called out by name.
    const allReserved = aircraftOptions.options.every((option) => (option.reason ?? '').toLowerCase().includes('reserved for the player'))
    return { ok: false, issue: { kind: allReserved ? 'all-reserved' : 'no-usable-aircraft' } }
  }

  // eligible.length > 0 was just confirmed above.
  const aircraft = eligible[0]!

  let built: DraftWeek = {}
  const usedDays = new Set<DayOfWeek>()
  let legsAdded = 0
  let daysUsed = 0

  for (const day of STARTER_DAYS) {
    if (usedDays.has(day)) continue

    try {
      // eslint-disable-next-line no-await-in-loop
      const chain = await buildChain(day, aircraft, built, usedDays, deps)
      if (chain) {
        built = chain.week
        for (const usedDay of chain.days) usedDays.add(usedDay)
        legsAdded += chain.legsAdded
        daysUsed += chain.days.length
      }
      // eslint-disable-next-line no-empty
    } catch {
      // A single day's round trip through the network failing (a transient fetch error) should
      // not sink every other day's chain - same resilience the old per-day generator had.
    }
  }

  if (legsAdded === 0) {
    return { ok: false, issue: { kind: 'no-legal-schedule' } }
  }

  return { ok: true, result: { week: built, legsAdded, daysUsed } }
}

/**
 * Builds one chain starting at `day`: as much of a same-day, up-to-four-leg pattern as the real
 * duty-hour and rest rules allow, or - when the outbound's own block time already rules out any
 * same-day return - a single leg on `day` closed by a matching return on `day + 1`. Returns `null`
 * (touching nothing) whenever a leg it would need to propose next cannot be confirmed legal first;
 * see this module's own doc for why that is the rule, not an edge case.
 */
async function buildChain(
  day: DayOfWeek,
  aircraft: { fleetAircraftId: string; registration: string },
  weekSoFar: DraftWeek,
  usedDays: ReadonlySet<DayOfWeek>,
  deps: StarterScheduleDeps,
): Promise<{ week: DraftWeek; days: DayOfWeek[]; legsAdded: number } | null> {
  const dayWithAircraft = setDayAircraft(weekSoFar, day, aircraft.fleetAircraftId, aircraft.registration)

  const outboundOptions = await deps.fetchLegOptions(day, STARTER_TIME, aircraft.fleetAircraftId, draftWeekToInput(dayWithAircraft))
  const outboundPick = legalOptionOf(outboundOptions.legal)
  if (!outboundPick) return null

  const outboundStart = timeToMinutes(`${STARTER_TIME}:00`)
  const outboundBlock = outboundPick.blockMinutes ?? 60
  let week = addLegToDay(dayWithAircraft, day, draftLegFromOption(outboundPick, STARTER_TIME, outboundBlock))

  const returnStart = outboundStart + outboundBlock + LEG_GAP_MINUTES
  if (returnStart < MINUTES_PER_DAY) {
    const returnTime = formatTime(returnStart)
    const returnOptions = await deps.fetchLegOptions(day, returnTime, aircraft.fleetAircraftId, draftWeekToInput(week))
    const returnPick = findReturnOption(returnOptions.legal, outboundPick)

    if (returnPick) {
      const returnBlock = returnPick.blockMinutes ?? 60
      week = addLegToDay(week, day, draftLegFromOption(returnPick, returnTime, returnBlock))
      let legs = 2

      // A same-day return closed the day already - try ONE more out-and-back on the same route
      // (the cap is two round trips; see MAX_LEGS_PER_GENERATED_DAY), but only keep it if BOTH its
      // legs check out. A day this generator hands back is never left mid-round-trip.
      const secondOutStart = returnStart + returnBlock + LEG_GAP_MINUTES
      if (legs < MAX_LEGS_PER_GENERATED_DAY && secondOutStart < MINUTES_PER_DAY) {
        const secondLegs = await tryAddSecondRoundTrip(day, aircraft.fleetAircraftId, week, outboundPick, secondOutStart, deps)
        if (secondLegs) {
          week = secondLegs.week
          legs = 4
        }
      }

      return { week, days: [day], legsAdded: legs }
    }
  }

  // No same-day return checks out - this is the long-haul shape: the day's only leg is the
  // outbound, closed (if at all) by a matching return on the very next day.
  const nextDay = (day + 1) as DayOfWeek
  if (!STARTER_DAYS.includes(nextDay) || usedDays.has(nextDay)) {
    return null
  }

  let nextWeek = setDayAircraft(week, nextDay, aircraft.fleetAircraftId, aircraft.registration)
  const nextDayOptions = await deps.fetchLegOptions(nextDay, STARTER_TIME, aircraft.fleetAircraftId, draftWeekToInput(nextWeek))
  const nextReturnPick = findReturnOption(nextDayOptions.legal, outboundPick)
  if (!nextReturnPick) {
    // Genuinely a dangling one-way at this point - never propose it (see this module's own doc).
    return null
  }

  const nextReturnBlock = nextReturnPick.blockMinutes ?? 60
  nextWeek = addLegToDay(nextWeek, nextDay, draftLegFromOption(nextReturnPick, STARTER_TIME, nextReturnBlock))

  return { week: nextWeek, days: [day, nextDay], legsAdded: 2 }
}

/** The third and fourth legs of a day already closed once - both-or-nothing, so a duty day this
 *  generator hands back never ends on an unmatched third leg. */
async function tryAddSecondRoundTrip(
  day: DayOfWeek,
  fleetAircraftId: string,
  weekAfterFirstRoundTrip: DraftWeek,
  firstOutbound: LegalLegOption,
  secondOutStart: number,
  deps: StarterScheduleDeps,
): Promise<{ week: DraftWeek } | null> {
  const secondOutTime = formatTime(secondOutStart)
  const secondOutOptions = await deps.fetchLegOptions(day, secondOutTime, fleetAircraftId, draftWeekToInput(weekAfterFirstRoundTrip))
  // Same route as the first round trip, deliberately - simplest, most predictable pattern for a
  // starter schedule, and it is exactly what a short sector flown twice a day looks like in
  // practice (e.g. the two-hop EGKK <-> EGPH day this feature was written for).
  const secondOutPick = secondOutOptions.legal.find((option) => option.routeId === firstOutbound.routeId && option.warnings.length === 0)
  if (!secondOutPick) return null

  const secondOutBlock = secondOutPick.blockMinutes ?? 60
  const secondReturnStart = secondOutStart + secondOutBlock + LEG_GAP_MINUTES
  if (secondReturnStart >= MINUTES_PER_DAY) return null

  const withThirdLeg = addLegToDay(weekAfterFirstRoundTrip, day, draftLegFromOption(secondOutPick, secondOutTime, secondOutBlock))
  const secondReturnTime = formatTime(secondReturnStart)
  const secondReturnOptions = await deps.fetchLegOptions(day, secondReturnTime, fleetAircraftId, draftWeekToInput(withThirdLeg))
  const secondReturnPick = findReturnOption(secondReturnOptions.legal, secondOutPick)
  if (!secondReturnPick) return null

  const secondReturnBlock = secondReturnPick.blockMinutes ?? 60
  const week = addLegToDay(withThirdLeg, day, draftLegFromOption(secondReturnPick, secondReturnTime, secondReturnBlock))
  return { week }
}

/** The first legal option with no warning attached - a warning means picking it leaves something
 *  unresolved elsewhere in the week (see LegalLegOption's own doc), and a generator's whole job is
 *  to hand back something that is already fully resolved, never a promise the player has to keep. */
function legalOptionOf(options: LegalLegOption[]): LegalLegOption | undefined {
  return options.find((option) => option.warnings.length === 0)
}

/** The option that flies the exact reverse of `outbound` - the only shape this generator ever
 *  proposes as a "return", same as the single-round-trip generator before it. */
function findReturnOption(options: LegalLegOption[], outbound: LegalLegOption): LegalLegOption | undefined {
  return options.find(
    (option) => option.departureIcao === outbound.arrivalIcao && option.arrivalIcao === outbound.departureIcao && option.warnings.length === 0,
  )
}

function formatTime(totalMinutes: number): string {
  return minutesToTime(totalMinutes).slice(0, 5)
}
