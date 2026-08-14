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
 * Mornings, every day of the week: every chain this generator proposes starts at {@link
 * STARTER_TIME} on one of {@link STARTER_DAYS}. What changed is what happens after that first
 * departure.
 *
 * <b>More than one aircraft is tried.</b> The generator ranks every eligible aircraft (see {@link
 * rankAircraftForStarterSchedule}) and works down the list until one yields a legal week, rather
 * than giving up if the first one cannot produce a chain. Real-use defect, 2026-08-13: an airline
 * with three aircraft was told no legal starter schedule existed at all, because the only aircraft
 * ever tried was one parked away from every route and already flown hard by another pilot - while a
 * second, idle airframe sitting at the hub every route touches would have succeeded immediately.
 *
 * <b>The route is chosen for what it earns, not just for being allowed.</b> Every option this
 * module considers is one the backend has already called legal; among those it takes the most
 * valuable rather than the first (see {@link scoreLegOption}). This is what stops every virtual
 * pilot in an airline being handed the same week: they used to all solve the identical "what is
 * legal from here" question and unavoidably get the identical answer. Ranking never widens what is
 * on offer - an option carrying an alert is still refused, a base airport is still fixed for the
 * whole week, and a day the backend will not close is still abandoned.
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
 * - it looks for a legal return on the VERY NEXT day instead (wrapping at the end of the week, since
 * the pattern repeats), which is what "one out-and-back across two days" means for a long-haul pair.
 * If no legal return
 * exists there either, the whole chain is abandoned and NEITHER day is touched - a dangling
 * one-way leg would fail week-closure at save, so one is never proposed.
 */

/** Days this generator proposes legs on - the whole week, Monday through Sunday (user's decision,
 *  2026-08-13; it used to stop at Friday). Nothing about seven days needs a relaxed rule: each day
 *  the generator builds is a closed out-and-back that leaves the aircraft back where it started, so
 *  the next day's 08:00 departure is legal for exactly the same reason the second day's always was,
 *  the week still closes on itself, and a duty day ending mid-afternoon leaves far more than the
 *  10-hour minimum rest before the next one. A day that genuinely will not fit is simply left empty
 *  - see {@link buildStarterSchedule} on falling back to the best legal week rather than failing. */
export const STARTER_DAYS: DayOfWeek[] = [1, 2, 3, 4, 5, 6, 0]

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

/**
 * Mirrors `SchedulingConfig.MaxDutyHoursPerDay` (13 hours, backend) purely so {@link estimateDayShape}
 * can guess how much of a duty day a sector would fill when RANKING routes.
 *
 * This number never decides what is proposed. Every leg the generator actually offers is still
 * confirmed legal by the backend, which applies the real duty rule against the real config; if this
 * constant ever drifted from that config the only consequence would be two routes ordered slightly
 * wrongly, never an illegal leg or a widened rule. It is here because ignoring duty length entirely
 * would systematically over-rate long sectors - a 5-hour sector reads as three legs a day if you
 * only ask "does the next departure still land before midnight", when the real answer is two.
 */
const DUTY_ESTIMATE_MINUTES = 13 * 60

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

/**
 * Why the generator picked what it picked, in the handful of facts a one-sentence explanation
 * needs. Deliberately structured rather than a finished string: money has to be rendered in the
 * player's chosen currency (see `useSettings().fmt.money`), which is a display concern this module
 * has no business knowing about. Null when the backend supplied no profit figures at all, in which
 * case there is genuinely nothing to explain beyond "it was legal" - see {@link scoreLegOption}.
 */
export interface StarterScheduleReason {
  registration: string
  departureIcao: string
  arrivalIcao: string
  /** One sector's expected net profit in base currency, straight from the backend's economy. */
  profitPerSector: number
  /** How many legs other pilots already fly on this exact leg. Zero for the ordinary case; above
   *  zero means this was chosen despite a market it now shares, which is worth saying out loud. */
  otherPilotLegs: number
}

export interface StarterScheduleBuilt {
  week: DraftWeek
  legsAdded: number
  daysUsed: number
  /** See {@link StarterScheduleReason} - null when no option carried a profit figure. */
  reason: StarterScheduleReason | null
}

export type StarterScheduleOutcome = { ok: true; result: StarterScheduleBuilt } | { ok: false; issue: StarterScheduleIssue }

/** The only thing this module needs to know about a route: where it departs from. Enough to tell an
 *  aircraft parked where the airline actually flies from one parked somewhere it cannot start a
 *  chain at all - see {@link rankAircraftForStarterSchedule}. */
export interface StarterScheduleRoute {
  departureIcao: string
}

/**
 * How much of a duty day one out-and-back on a sector of this length would fill, using EXACTLY the
 * arithmetic {@link buildChain} already uses to place the next departure (08:00 start, block time
 * plus {@link LEG_GAP_MINUTES}, must still depart the same calendar day, at most
 * {@link MAX_LEGS_PER_GENERATED_DAY} legs), plus the duty-length estimate above.
 *
 * A ranking aid, never a rule: the backend still decides every leg. It exists because "most
 * profitable" is a question about a WEEK, not a sector - a 9-hour transatlantic that nets four
 * times what a 65-minute hop does is still the worse pattern if the hop closes two round trips a
 * day and the transatlantic takes two days to close one. Returning legs-per-cycle and
 * days-per-cycle separately lets {@link scoreLegOption} compare the two on the same footing.
 */
function estimateDayShape(blockMinutes: number): { legs: number; days: number } {
  const start = timeToMinutes(`${STARTER_TIME}:00`)
  const dutyEnd = start + DUTY_ESTIMATE_MINUTES

  const fits = (departure: number) => departure < MINUTES_PER_DAY && departure + blockMinutes <= dutyEnd

  const returnStart = start + blockMinutes + LEG_GAP_MINUTES
  if (!fits(returnStart)) {
    // The long-haul shape: one leg out, closed by a return the NEXT day. Two legs, two days.
    return { legs: 2, days: 2 }
  }

  const secondOutStart = returnStart + blockMinutes + LEG_GAP_MINUTES
  const secondReturnStart = secondOutStart + blockMinutes + LEG_GAP_MINUTES
  const twoRoundTrips = MAX_LEGS_PER_GENERATED_DAY >= 4 && fits(secondOutStart) && fits(secondReturnStart)
  return { legs: twoRoundTrips ? 4 : 2, days: 1 }
}

/**
 * What one legal option is worth per day of the aircraft's week. Three things, in one number a
 * player can be told in a sentence:
 *
 * 1. <b>What the sector earns.</b> `expectedNetProfit` is the backend's own economy engine - the
 *    same fare, demand, fee and fuel model that eventually posts to the ledger - resolved against
 *    THIS airframe, so seats, weight and block time all count. That is what makes the answer
 *    different for a 70-seat turboprop and a 180-seat narrowbody standing at the same gate.
 * 2. <b>How much of the week it can fill.</b> Multiplied by the legs a day of it would carry and
 *    divided by the days that costs (see {@link estimateDayShape}), so a magnificent sector that
 *    leaves the aircraft parked for a day does not beat an ordinary one flown four times.
 * 3. <b>Who else is already flying it.</b> A city pair's market is finite, so a leg N other pilots
 *    already work is shared N + 1 ways. This is a diminishing return, deliberately not a ban: the
 *    second pilot on a pair is worth less than the first, so an alternative that is anywhere near
 *    as good wins - but a pair that is genuinely the only thing worth flying is still flown, which
 *    is exactly the fall-back the design calls for.
 *
 * `null` means "this option cannot be scored" (an older server, or a route the backend could not
 * price), never "worth nothing" - callers must fall back to their previous ordering rather than
 * treating an unpriced option as a zero-profit one.
 */
export function scoreLegOption(option: LegalLegOption): number | null {
  const profit = option.expectedNetProfit
  if (profit === null || profit === undefined || !Number.isFinite(profit)) return null

  const shape = estimateDayShape(option.blockMinutes ?? 60)
  const sharedWith = Math.max(0, option.scheduledLegsThisWeek ?? 0)
  return (profit * shape.legs) / shape.days / (1 + sharedWith)
}

/**
 * Every eligible aircraft, best first guess first. Deliberately a total order with an explicit
 * final tiebreak, so the generator is still deterministic (the tests rely on that).
 *
 * 1. <b>Can it start a chain from where it is standing?</b> An aircraft parked at an airport some
 *    route departs from can fly today; one parked anywhere else cannot legally take a first leg at
 *    all, because the backend anchors a week's earliest leg to the airframe's real position. This
 *    is the difference between the two identical ATRs in the defect this ordering was written for.
 * 2. <b>How contended is it?</b> Fewest already-scheduled legs elsewhere in the airline first. A
 *    heavily-booked airframe is exactly the one whose every candidate leg comes back carrying a
 *    warning (double-booking, turnaround) and is therefore refused by {@link legalOptionOf} - so
 *    trying the idle one first is not a preference, it is the likelier success.
 * 3. <b>Whatever order the server gave.</b> `GetAircraftOptionsAsync` returns the fleet ordered by
 *    `CreatedUtc`, i.e. the order the player happened to acquire the aircraft in - which carries no
 *    information about whether it can fly this week, and is precisely why relying on it alone
 *    produced "no legal schedule" for an airline that plainly had one. Kept only as a stable
 *    tiebreak so two equally-good aircraft always resolve the same way.
 */
export function rankAircraftForStarterSchedule<T extends { locationIcao: string; scheduledLegsThisWeek: number }>(
  eligible: readonly T[],
  routes: readonly StarterScheduleRoute[],
): T[] {
  const departureIcaos = new Set(routes.map((route) => route.departureIcao.toUpperCase()))
  return eligible
    .map((option, index) => ({ option, index }))
    .sort((a, b) => {
      const aCanStart = departureIcaos.has((a.option.locationIcao ?? '').toUpperCase()) ? 0 : 1
      const bCanStart = departureIcaos.has((b.option.locationIcao ?? '').toUpperCase()) ? 0 : 1
      if (aCanStart !== bCanStart) return aCanStart - bCanStart

      const aBusy = a.option.scheduledLegsThisWeek ?? 0
      const bBusy = b.option.scheduledLegsThisWeek ?? 0
      if (aBusy !== bBusy) return aBusy - bBusy

      return a.index - b.index
    })
    .map((entry) => entry.option)
}

/**
 * Builds the starter week. `routes` is passed in rather than fetched here - the caller
 * (ScheduleBuilder) already has the routes query loaded, and re-fetching it here would just be a
 * second source of truth for the same facts. Only `departureIcao` is read: whether the airline has
 * any routes at all, and which airports an aircraft could actually start a chain from.
 *
 * <b>Every eligible aircraft is tried, and the best legal week wins.</b> Working down {@link
 * rankAircraftForStarterSchedule}'s order, the first aircraft that produces any legs at all is the
 * one used. `no-legal-schedule` is only reported once EVERY eligible aircraft has been tried and
 * none of them yielded a single leg - it now means what it says, rather than "the one aircraft we
 * happened to ask about was busy". A partial week (some days legal, some not) is a success, not a
 * failure: the days that fit are handed back and the rest are simply left empty for the player.
 *
 * <b>Which ROUTE that aircraft flies is decided by {@link scoreLegOption}</b>, so two pilots with
 * two aircraft do not both end up on the airline's single most obvious city pair. Nothing about
 * that is random: the ranking is a pure function of figures the backend supplied, so the same
 * airline in the same state always suggests the same week.
 */
export async function buildStarterSchedule(
  routes: readonly StarterScheduleRoute[],
  deps: StarterScheduleDeps,
): Promise<StarterScheduleOutcome> {
  if (routes.length === 0) {
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

  // Every eligible aircraft, best first guess first - not just whichever the server listed first.
  for (const aircraft of rankAircraftForStarterSchedule(eligible, routes)) {
    // eslint-disable-next-line no-await-in-loop
    const built = await buildWeekFor(aircraft, deps)
    if (built.legsAdded > 0) {
      return { ok: true, result: built }
    }
  }

  // Genuinely nothing: every eligible aircraft was asked, on every day, and not one legal leg came
  // back.
  return { ok: false, issue: { kind: 'no-legal-schedule' } }
}

/**
 * One aircraft's best week: each day of {@link STARTER_DAYS} in turn, keeping whatever chains come
 * back legal and quietly leaving the days that do not. A day that will not fit is not a failure of
 * the week - a six-day week is a perfectly good starter schedule, and refusing to hand back the
 * five days that DID work because the sixth did not would be strictly worse for the player than
 * offering them the five.
 */
async function buildWeekFor(
  aircraft: { fleetAircraftId: string; registration: string },
  deps: StarterScheduleDeps,
): Promise<StarterScheduleBuilt> {
  let built: DraftWeek = {}
  const usedDays = new Set<DayOfWeek>()
  let legsAdded = 0
  let daysUsed = 0
  /** The option the FIRST chain led with - the one whose value actually decided this week's shape,
   *  and therefore the only honest thing to quote as the reason. Every later day departs the same
   *  base and is scored the same way, so in practice it is the same leg every day. */
  let reason: StarterScheduleReason | null = null

  // Every day this generator builds must be an out-and-back from the SAME airport, and that is the
  // whole reason its weeks close. Defect caught in testing, 2026-08-13: each day was built
  // independently against whatever the backend said the aircraft's position would be that morning -
  // which is not one fixed airport, because ANOTHER pilot's legs earlier in the week move the
  // airframe. The result was a week of individually-legal days based at two different airports
  // (Saturday round trips out of EGGD, Sunday's out of LFPG) that could not join up, and the
  // generated week was refused the moment it was saved - breaking this module's one real promise,
  // that what it hands back is always immediately saveable. Fixing the days to a single base makes
  // closure structural rather than something to hope for: end every day where you started, and you
  // necessarily end the WEEK where you started too.
  let baseIcao: string | null = null

  for (const day of STARTER_DAYS) {
    if (usedDays.has(day)) continue

    try {
      // eslint-disable-next-line no-await-in-loop
      const chain = await buildChain(day, aircraft, built, usedDays, deps, baseIcao)
      if (chain) {
        built = chain.week
        baseIcao ??= chain.baseIcao
        reason ??= reasonFor(aircraft.registration, chain.leadOption)
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

  return { week: built, legsAdded, daysUsed, reason }
}

/** The reason, or null when this option carried no profit figure - see {@link StarterScheduleReason}
 *  for why an unpriced option is left unexplained rather than explained with a made-up number. */
function reasonFor(registration: string, option: LegalLegOption): StarterScheduleReason | null {
  const profit = option.expectedNetProfit
  if (profit === null || profit === undefined || !Number.isFinite(profit)) return null
  return {
    registration,
    departureIcao: option.departureIcao,
    arrivalIcao: option.arrivalIcao,
    profitPerSector: profit,
    otherPilotLegs: Math.max(0, option.scheduledLegsThisWeek ?? 0),
  }
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
  /** The airport every day of this week departs from, once the first chain has established it -
   *  `null` only for that very first chain, which is free to start wherever the aircraft is. See
   *  {@link buildWeekFor} for why this is what keeps the generated week closed. */
  baseIcao: string | null,
): Promise<{ week: DraftWeek; days: DayOfWeek[]; legsAdded: number; baseIcao: string; leadOption: LegalLegOption } | null> {
  const dayWithAircraft = setDayAircraft(weekSoFar, day, aircraft.fleetAircraftId, aircraft.registration)

  const outboundOptions = await deps.fetchLegOptions(day, STARTER_TIME, aircraft.fleetAircraftId, draftWeekToInput(dayWithAircraft))
  const outboundPick = legalOptionOf(outboundOptions.legal, baseIcao)
  if (!outboundPick) return null
  const base = outboundPick.departureIcao

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

      return { week, days: [day], legsAdded: legs, baseIcao: base, leadOption: outboundPick }
    }
  }

  // No same-day return checks out - this is the long-haul shape: the day's only leg is the
  // outbound, closed (if at all) by a matching return on the very next day. The week is a repeating
  // cycle, so "the next day" after Saturday is Sunday and after Sunday is Monday - hence the wrap
  // rather than a bare `day + 1`, which used to fall off the end of the working week and abandon a
  // chain that had a perfectly good day to close on.
  const nextDay = (((day + 1) % 7) + 7) % 7 as DayOfWeek
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

  return { week: nextWeek, days: [day, nextDay], legsAdded: 2, baseIcao: base, leadOption: outboundPick }
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
  const sameRoute = (option: LegalLegOption) => option.routeId === firstOutbound.routeId
  const secondOutPick = secondOutOptions.legal.find((option) => sameRoute(option) && option.warnings.length === 0)
    ?? secondOutOptions.legal.find((option) => sameRoute(option) && !isBlockingWarning(option))
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

/**
 * A warning this generator must not pick past, as opposed to one it is about to resolve itself.
 *
 * `"alert"` is a genuine incompatibility with something already committed elsewhere - double-booking
 * the airframe, too little turnaround or rest, an over-long duty day. Nothing the generator does
 * next makes those untrue, so an option carrying one is never taken: a generated week has to be
 * immediately saveable, never a promise the player has to keep.
 *
 * `"info"` is the opposite: a continuity gap, meaning "this leaves the aircraft at X and a later leg
 * needs it back at Y - add the return after this one." That is not an unresolved promise here, it is
 * a description of the generator's very next action, since every chain it builds closes its own
 * round trip before the day is handed back (and abandons the day entirely if it cannot). Refusing
 * those made Sunday impossible to generate at all: the week is a cycle and Sunday sorts before
 * Monday, so a Sunday outbound ALWAYS reads as stranding the aircraft away from Monday's first leg
 * until its own return leg exists a moment later.
 */
function isBlockingWarning(option: LegalLegOption): boolean {
  return option.warnings.some((warning) => warning.severity === 'alert')
}

/**
 * The best legal option to lead a chain with. Two tiers, in this order, and the order matters:
 *
 * 1. <b>Encumbrance first.</b> A completely unencumbered option always beats one carrying even a
 *    resolvable warning, exactly as before profit entered into this. Profit never promotes a
 *    warned option over a clean one - what a leg is worth is a question about legal options, not a
 *    reason to prefer a more complicated one.
 * 2. <b>Then value.</b> Within a tier, the highest {@link scoreLegOption} wins. Options the backend
 *    could not price are not treated as worthless: if NOTHING in the tier can be scored the first
 *    one is taken, which is precisely the behaviour this function had before, so an older server or
 *    a world-data gap degrades to the old ordering rather than to a bad one.
 *
 * `baseIcao`, once the week has one, restricts this to legs departing that airport - see
 * {@link buildWeekFor} for why every day has to leave from the same place.
 */
function legalOptionOf(options: LegalLegOption[], baseIcao: string | null): LegalLegOption | undefined {
  const candidates = baseIcao === null
    ? options
    : options.filter((option) => option.departureIcao.toUpperCase() === baseIcao.toUpperCase())
  return mostValuableOf(candidates.filter((option) => option.warnings.length === 0))
    ?? mostValuableOf(candidates.filter((option) => !isBlockingWarning(option)))
}

/** Highest-scoring option in one tier, falling back to the first when none of them can be scored.
 *  Ties keep the earlier option, so the result is a total order and two identical runs always agree
 *  - the determinism the whole module is built on (see this module's own doc). */
function mostValuableOf(tier: LegalLegOption[]): LegalLegOption | undefined {
  let best: LegalLegOption | undefined
  let bestScore: number | null = null
  for (const option of tier) {
    const score = scoreLegOption(option)
    if (score === null) continue
    if (bestScore === null || score > bestScore) {
      best = option
      bestScore = score
    }
  }
  return best ?? tier[0]
}

/** The option that flies the exact reverse of `outbound` - the only shape this generator ever
 *  proposes as a "return", same as the single-round-trip generator before it. */
function findReturnOption(options: LegalLegOption[], outbound: LegalLegOption): LegalLegOption | undefined {
  const reverses = (option: LegalLegOption) =>
    option.departureIcao === outbound.arrivalIcao && option.arrivalIcao === outbound.departureIcao
  return options.find((option) => reverses(option) && option.warnings.length === 0)
    ?? options.find((option) => reverses(option) && !isBlockingWarning(option))
}

function formatTime(totalMinutes: number): string {
  return minutesToTime(totalMinutes).slice(0, 5)
}
