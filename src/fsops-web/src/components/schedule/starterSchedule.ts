import {
  addLegToDay,
  draftLegFromOption,
  draftWeekToInput,
  setDayAircraft,
  type DraftWeek,
} from './draftEntry'
import type {
  AircraftOptionsResponse,
  DayOfWeek,
  DutyDayInput,
  LegalLegOption,
  LegOptionsResponse,
  SchedulingLimits,
} from '@/types/schedule'
import { minutesToTime, timeToMinutes } from '@/types/schedule'

/**
 * The "Suggest a starter schedule" generator - pure orchestration logic, deliberately separated
 * from ScheduleBuilder so it can be driven by fake `deps` in tests without a real server or a
 * mounted component. Every leg it proposes is checked against the SAME leg-options endpoint the
 * manual picker uses (see PilotEndpoints.GetLegOptionsAsync), so nothing here re-implements or
 * relaxes the real duty/rest/continuity/range/runway rules - it only ever accepts what the backend
 * already calls legal, which is what keeps the result always immediately saveable.
 *
 * Mornings, every day of the week: every day this generator builds starts at {@link STARTER_TIME}
 * on one of {@link STARTER_DAYS}.
 *
 * <b>It fills the duty day, and stops because a rule stops it.</b> There is no leg-count ceiling.
 * The day keeps taking legs for as long as the backend keeps calling the next one legal, and the
 * thing that ends it is the real constraint - almost always the 13-hour maximum duty day, sometimes
 * turnaround or rest against something already committed elsewhere. Which one it was is reported
 * (see {@link StarterScheduleStop}) rather than left for the player to infer, because "it stopped at
 * four" is not an answer and "duty hours ran out at 20:05" is. Until 2026-08-20 there WAS a ceiling
 * here - four legs, two return trips - and the player's own hand-built weeks ran eight and nine legs
 * a day through the manual picker, which is the same rules reaching a different answer. The ceiling
 * was never a legality rule; it was a guess, and it was costing the player hours of flying a day
 * that they were putting back in by hand.
 *
 * <b>A day may end away from base, and the next day picks up there.</b> The player's hand-built
 * weeks chain across airports (Monday ends EGPH, Tuesday starts EGPH), and so does this now - which
 * is what lets a day take an ODD number of legs instead of being rounded down to a whole number of
 * round trips. See {@link commitClosedPrefix} for the single thing that makes that safe.
 *
 * <b>More than one aircraft is tried, but a generated week only ever uses ONE.</b> The generator
 * ranks every eligible aircraft (see {@link rankAircraftForStarterSchedule}) and works down the
 * list until one yields a legal week, rather than giving up if the first one cannot produce a
 * chain. Real-use defect, 2026-08-13: an airline with three aircraft was told no legal starter
 * schedule existed at all, because the only aircraft ever tried was one parked away from every
 * route and already flown hard by another pilot - while a second, idle airframe sitting at the hub
 * every route touches would have succeeded immediately. "Tried" means tried from scratch: an
 * airframe that produces no legs at all is abandoned whole and the next one starts a fresh week.
 * Legs from two airframes are never mixed into one suggestion.
 *
 * <b>One pilot, one aircraft - a discipline this generator observes, never a rule the app
 * enforces</b> (user's decision, 2026-08-20, with the "unless the user manually builds the schedule"
 * that makes it a preference rather than a law). Reaching for a second airframe when the chosen one
 * runs out of legal legs would squeeze out more sectors and is deliberately not done. The manual
 * picker stays free to mix - `PilotScheduleValidator` requires one aircraft per DUTY DAY, not per
 * week, and swapping mid-week is a legitimate thing to want when an airframe is contended or
 * grounded. Do not "fix" the picker to match this module: making it a hard rule would render every
 * already-saved mixed week unsavable, which is precisely the trap the 2026-08-13 fix exists to
 * avoid - a rolling pattern must always stay editable. Same family as the leg-count ceiling this
 * module used to carry: a choice about what to SUGGEST, not about what is allowed.
 *
 * <b>The route is chosen for what it earns, not just for being allowed.</b> Every option this
 * module considers is one the backend has already called legal; among those it takes the most
 * valuable rather than the first (see {@link scoreLegOption}). This is what stops every virtual
 * pilot in an airline being handed the same week: they used to all solve the identical "what is
 * legal from here" question and unavoidably get the identical answer. Ranking never widens what is
 * on offer - an option carrying an alert is still refused, and a day the backend will not extend is
 * still left where it is.
 *
 * <b>The week is one shuttle on the pair the first leg establishes.</b> The opening leg is chosen
 * for value; every leg after it is the exact reverse of the one before (see {@link
 * findReturnOption}), all the way round the week. That is deliberately the same route every day the
 * old generator already flew - it is the simplest, most predictable shape for a suggestion, and it
 * is what makes closure a question of parity rather than of graph search.
 *
 * <b>Block time is real, not a guess.</b> Every candidate's `blockMinutes` comes back from
 * leg-options resolved against the actual aircraft chosen for the day (see that endpoint's own
 * "K34" remarks) - the same figure a save would resolve to. The generator adds it, plus a
 * turnaround, to place the NEXT candidate's departure. The day's own duty-hour, turnaround and rest
 * checks are what genuinely decide whether that leg fits; the arithmetic here only keeps the times
 * physically sane before asking.
 *
 * <b>The turnaround varies, and it is chosen before the leg is validated.</b> Each turn is drawn
 * from a range above the configured minimum (see {@link TURNAROUND_SPREAD_MINUTES}) so a week does
 * not read as machine-stamped, and it is decided BEFORE the next candidate is put to the backend -
 * so the times validated are the times proposed, and there is no window in which a day was packed
 * assuming one turn and saved with another. The draw is deterministic
 * ({@link turnaroundMinutesFor}), so the same airline in the same state still suggests the same week
 * twice.
 *
 * <b>Every limit it works against is the backend's own.</b> The maximum duty day and the minimum
 * turnaround arrive with each leg-options answer (see {@link StarterScheduleLimits}) rather than
 * being kept here as a second copy free to drift from what the validator enforces.
 */

/** Days this generator proposes legs on - the whole week, Monday through Sunday (user's decision,
 *  2026-08-13; it used to stop at Friday). Nothing about seven days needs a relaxed rule: every day
 *  departs at {@link STARTER_TIME} and no day's duty may exceed the backend's 13-hour maximum, so a
 *  duty day can never end later than 21:00 and the next morning's 08:00 departure always clears the
 *  10-hour minimum rest with an hour to spare. A day that genuinely will not fit is simply left
 *  empty - see {@link buildStarterSchedule} on falling back to the best legal week rather than
 *  failing. */
export const STARTER_DAYS: DayOfWeek[] = [1, 2, 3, 4, 5, 6, 0]

/** Every day's first departure. Not a promise every leg lands in the morning - a day's later legs
 *  depart whenever the real rules put them, which on a short sector runs well into the evening. */
export const STARTER_TIME = '08:00'

/**
 * How far ABOVE the configured minimum turnaround a generated turn may fall (user's decision,
 * 2026-08-20: "make it random between 30 and 40 mins" - with the shipped
 * `SchedulingConfig.MinTurnaroundMinutes` of 30, a spread of 10 is exactly that range).
 *
 * <b>Expressed as a spread above the real floor, never as two absolute numbers.</b> The floor comes
 * from the backend with every leg-options answer (see {@link StarterScheduleLimits}), so a turn this
 * module proposes can never drop below what the validator enforces - if the configured minimum ever
 * rose to 35, the range would move to 35-45 rather than silently proposing illegal turns.
 *
 * <b>Why vary it at all.</b> A fixed buffer stamps every day out identically, and it is also the
 * whole remaining difference between what this generator reaches and what a player building by hand
 * reaches: on a 65-minute sector a 13-hour duty day holds seven legs at a 45-minute turn and eight
 * at a 30-minute one. Varying across 30-40 lands honestly in between - most days seven, some eight,
 * depending on how the turns fall - and reads like an airline rather than a machine.
 *
 * The variation is deterministic (see {@link turnaroundMinutesFor}), never `Math.random()`: this
 * whole module is built so the same airline in the same state suggests the same week twice, and a
 * player clicking "suggest" twice must not get two unexplainable answers.
 */
const TURNAROUND_SPREAD_MINUTES = 10

const MINUTES_PER_DAY = 24 * 60

/**
 * The duty and turnaround limits this generator works against - the backend's own, sent with every
 * leg-options answer, never a second copy kept here.
 *
 * Neither number decides what is PROPOSED: the generator fills a duty day until the backend refuses
 * the next leg, so legality is always the backend's answer to a question about real times. What
 * these are for is spacing departures ({@link turnaroundMinutesFor}) and ranking routes by how much
 * of a day a sector would fill ({@link estimateLegsPerDay}). Both would still work with a stale
 * figure - the worst a drifted duty ceiling could do is order two routes slightly wrongly - but a
 * stale turnaround FLOOR would propose turns the validator refuses, which is exactly the kind of
 * disagreement between generator and validator this feature cannot afford.
 */
export interface StarterScheduleLimits {
  maxDutyMinutes: number
  minTurnaroundMinutes: number
}

/** What the shipped `SchedulingConfig` says, used only until the first leg-options answer arrives
 *  (and by an older server that does not send its limits at all). Kept in step with
 *  EconomyConfig.Scheduling's own defaults. */
export const DEFAULT_STARTER_LIMITS: StarterScheduleLimits = {
  maxDutyMinutes: 13 * 60,
  minTurnaroundMinutes: 30,
}

/** Reads the backend's limits off a leg-options answer, falling back to whatever the caller was
 *  already using rather than to nothing - an older server simply keeps the shipped defaults. */
function limitsFrom(response: LegOptionsResponse, fallback: StarterScheduleLimits): StarterScheduleLimits {
  const sent: SchedulingLimits | undefined = response.scheduling
  if (!sent || !Number.isFinite(sent.maxDutyHoursPerDay) || !Number.isFinite(sent.minTurnaroundMinutes)) {
    return fallback
  }
  return {
    maxDutyMinutes: Math.round(sent.maxDutyHoursPerDay * 60),
    minTurnaroundMinutes: Math.round(sent.minTurnaroundMinutes),
  }
}

/**
 * The turnaround between one proposed leg's arrival and the next one's departure: a whole number of
 * minutes somewhere in `[floor, floor + TURNAROUND_SPREAD_MINUTES]`, varied per leg and completely
 * reproducible.
 *
 * <b>Deterministic on purpose.</b> `Math.random()` would break the promise the rest of this module
 * is built on - the same airline in the same state suggests the same week, tests assert exact
 * times, and a player clicking "suggest" twice gets one answer rather than two. The material it
 * hashes is stable across runs but different across the things that should differ: the airframe
 * (which is what actually distinguishes one pilot's suggestion from another's, since the ranking
 * hands different pilots different aircraft), the day of the week, and the leg's position in the
 * day. If a future caller wants variation per PILOT on the same airframe, the pilot id is the
 * obvious extra ingredient and this is where it goes.
 *
 * <b>Never below the floor</b>, which is the configured `MinTurnaroundMinutes`, because the result
 * is that floor plus a non-negative remainder.
 */
function turnaroundMinutesFor(fleetAircraftId: string, day: DayOfWeek, legIndex: number, floorMinutes: number): number {
  return floorMinutes + (hash32(`${fleetAircraftId}|${day}|${legIndex}`) % (TURNAROUND_SPREAD_MINUTES + 1))
}

/** FNV-1a, 32-bit, unsigned. A hash, not a cipher - all that is wanted here is that neighbouring
 *  inputs land on unrelated outputs, so consecutive legs do not all turn in the same time. */
function hash32(text: string): number {
  let hash = 0x811c9dc5
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }
  return hash >>> 0
}

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

/** What actually ended the fullest day the generator built - see {@link StarterScheduleStop}. */
export type StarterScheduleStopKind =
  /** The backend refused the next leg and said why. `reason` is its own sentence, verbatim - a duty
   *  day at its maximum, a turnaround or rest gap against something already committed, and so on. */
  | 'rule'
  /** Nothing came back that could continue the chain and the backend offered no reason for the
   *  route we wanted - normally "there is simply no return leg on this pair at that hour". */
  | 'nowhere-to-go'
  /** The day had room for another leg and the rules allowed it, but taking it would have left the
   *  aircraft away from where the week began with no day left to bring it home - so it was cut (see
   *  {@link commitClosedPrefix}). Only ever the last day of a week, and only ever by the odd leg. */
  | 'week-closure'
  /** The next departure would have fallen past midnight. Effectively unreachable while duty is
   *  capped at 13 hours from an 08:00 start, but it is what guarantees this loop terminates. */
  | 'calendar-day'
  /** The legality check itself failed (the network, not a rule) - the day keeps whatever it had. */
  | 'network'

/**
 * Where the fullest generated day stopped, and why. The player is owed this: a generator that fills
 * a duty day to its limit and says nothing leaves them unable to tell "this is everything the rules
 * allow" from "this is all it bothered to look for" - which is exactly the complaint the leg-count
 * ceiling produced. Reported for the day that took the MOST legs, since that is the day genuinely up
 * against a limit; a day that took none was not stopped by anything, it never started.
 */
export interface StarterScheduleStop {
  day: DayOfWeek
  /** How many legs that day ended up with, after {@link commitClosedPrefix}. */
  legs: number
  kind: StarterScheduleStopKind
  /** The backend's own sentence, rendered verbatim (same convention as `IllegalLegOption.reason`).
   *  Non-null exactly when `kind` is `'rule'`. */
  reason: string | null
}

export interface StarterScheduleBuilt {
  week: DraftWeek
  legsAdded: number
  daysUsed: number
  /** Total block time across every proposed leg - the "hours in the air" the player actually cares
   *  about, and the honest unit for "how much of this week is flying". */
  blockMinutes: number
  /** See {@link StarterScheduleReason} - null when no option carried a profit figure. */
  reason: StarterScheduleReason | null
  /** See {@link StarterScheduleStop} - null only for a week that ended up with no legs at all. */
  stop: StarterScheduleStop | null
}

export type StarterScheduleOutcome = { ok: true; result: StarterScheduleBuilt } | { ok: false; issue: StarterScheduleIssue }

/** The only thing this module needs to know about a route: where it departs from. Enough to tell an
 *  aircraft parked where the airline actually flies from one parked somewhere it cannot start a
 *  chain at all - see {@link rankAircraftForStarterSchedule}. */
export interface StarterScheduleRoute {
  departureIcao: string
}

/**
 * How many legs of a sector this long one duty day would hold, using the same arithmetic {@link
 * fillDay} uses to place the next departure (08:00 start, block time plus a turnaround), stopped by
 * the configured maximum duty day.
 *
 * A ranking aid, never a rule: the backend still decides every leg. It exists because "most
 * profitable" is a question about a WEEK, not a sector - a 9-hour transatlantic that nets four
 * times what a 65-minute hop does is still the worse pattern if the hop flies seven times a day and
 * the transatlantic flies once.
 *
 * Uses the MIDDLE of the turnaround range rather than a specific leg's own turn, deliberately: this
 * question is asked before any leg exists, about a sector rather than a slot, and an answer that
 * moved with whichever turn happened to be drawn first would make two equally good routes rank
 * differently for no reason a player could ever see.
 *
 * Never returns zero: a sector too long for even one leg inside a duty day is not schedulable at
 * all, and the honest answer to "how should it be RANKED" is still "one leg a day", not a division
 * by zero.
 */
function estimateLegsPerDay(blockMinutes: number, limits: StarterScheduleLimits): number {
  const start = timeToMinutes(`${STARTER_TIME}:00`)
  const dutyEnd = start + limits.maxDutyMinutes
  const typicalTurnaround = limits.minTurnaroundMinutes + Math.floor(TURNAROUND_SPREAD_MINUTES / 2)

  let legs = 0
  let departure = start
  while (departure < MINUTES_PER_DAY && departure + blockMinutes <= dutyEnd) {
    legs += 1
    departure += blockMinutes + typicalTurnaround
  }

  return Math.max(1, legs)
}

/**
 * What one legal option is worth per day of the aircraft's week. Three things, in one number a
 * player can be told in a sentence:
 *
 * 1. <b>What the sector earns.</b> `expectedNetProfit` is the backend's own economy engine - the
 *    same fare, demand, fee and fuel model that eventually posts to the ledger - resolved against
 *    THIS airframe, so seats, weight and block time all count. That is what makes the answer
 *    different for a 70-seat turboprop and a 180-seat narrowbody standing at the same gate.
 * 2. <b>How much of the week it can fill.</b> Multiplied by the legs a day of it would carry (see
 *    {@link estimateLegsPerDay}), so a magnificent sector flown once a day does not beat an
 *    ordinary one flown seven times.
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
export function scoreLegOption(option: LegalLegOption, limits: StarterScheduleLimits = DEFAULT_STARTER_LIMITS): number | null {
  const profit = option.expectedNetProfit
  if (profit === null || profit === undefined || !Number.isFinite(profit)) return null

  const legsPerDay = estimateLegsPerDay(option.blockMinutes ?? 60, limits)
  const sharedWith = Math.max(0, option.scheduledLegsThisWeek ?? 0)
  return (profit * legsPerDay) / (1 + sharedWith)
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

/** One leg the generator has decided to propose, in chain order - kept alongside the {@link
 *  DraftWeek} being assembled so the week can be re-cut at the end (see {@link commitClosedPrefix})
 *  without re-deriving which leg came after which from departure times. */
interface PlacedLeg {
  day: DayOfWeek
  option: LegalLegOption
  /** "HH:mm" - the form {@link draftLegFromOption} expects. */
  time: string
  blockMinutes: number
}

/** How one day's fill ended, before the week is cut to close - see {@link StarterScheduleStop}. */
interface DayStop {
  kind: StarterScheduleStopKind
  reason: string | null
}

/**
 * One aircraft's best week: each day of {@link STARTER_DAYS} in turn, filled until a rule stops it,
 * each day picking up wherever the last one left the airframe. A day that will not fit is not a
 * failure of the week - a six-day week is a perfectly good starter schedule, and refusing to hand
 * back the five days that DID work because the sixth did not would be strictly worse for the player
 * than offering them the five.
 */
async function buildWeekFor(
  aircraft: { fleetAircraftId: string; registration: string },
  deps: StarterScheduleDeps,
): Promise<StarterScheduleBuilt> {
  let week: DraftWeek = {}
  const placed: PlacedLeg[] = []
  const stops = new Map<DayOfWeek, DayStop>()
  /** The last leg proposed anywhere in the week. Every leg after the first is its exact reverse
   *  (see this module's own doc), which is both what keeps the week a predictable shuttle and what
   *  keeps the airframe's position knowable one leg at a time. */
  let previous: LegalLegOption | null = null
  /** The option the FIRST leg led with - the one whose value actually decided this week's shape,
   *  and therefore the only honest thing to quote as the reason. */
  let lead: LegalLegOption | null = null
  let limits = DEFAULT_STARTER_LIMITS

  for (const day of STARTER_DAYS) {
    // eslint-disable-next-line no-await-in-loop
    const filled = await fillDay(day, aircraft, week, previous, limits, deps)
    limits = filled.limits
    if (filled.placed.length === 0) {
      // Nothing on this day - leave it out of the draft entirely rather than handing back a day
      // with an aircraft chosen and no legs on it.
      continue
    }

    week = filled.week
    placed.push(...filled.placed)
    stops.set(day, filled.stop)
    previous = filled.placed[filled.placed.length - 1]!.option
    lead ??= filled.placed[0]!.option
  }

  return commitClosedPrefix(placed, stops, aircraft, lead)
}

/**
 * Fills one duty day: legs at {@link STARTER_TIME} and then every block-time-plus-turnaround after
 * it, for as long as the backend keeps calling the next one legal.
 *
 * <b>Nothing here decides what is legal.</b> The loop's only own stopping condition is the calendar
 * day, which cannot be crossed by a departure time and is what guarantees termination (every
 * iteration advances the clock by at least one turnaround, which cannot be zero). Every other stop is the
 * backend refusing the next leg, and its refusal is captured verbatim so the player can be told
 * which rule ran out.
 *
 * Returns the day untouched, with no legs and no aircraft, when nothing came back legal at all -
 * a day the aircraft cannot fly is left empty for the player rather than half-claimed.
 */
async function fillDay(
  day: DayOfWeek,
  aircraft: { fleetAircraftId: string; registration: string },
  weekSoFar: DraftWeek,
  /** The last leg flown anywhere in the week, or null for the very first leg of the week - which is
   *  the only one free to be chosen for value rather than for reversing what came before. */
  previous: LegalLegOption | null,
  limits: StarterScheduleLimits,
  deps: StarterScheduleDeps,
): Promise<{ week: DraftWeek; placed: PlacedLeg[]; stop: DayStop; limits: StarterScheduleLimits }> {
  let week = setDayAircraft(weekSoFar, day, aircraft.fleetAircraftId, aircraft.registration)
  const placed: PlacedLeg[] = []
  const routesFlownToday = new Set<string>()
  let known = limits
  let last = previous
  let departure = timeToMinutes(`${STARTER_TIME}:00`)
  let stop: DayStop = { kind: 'calendar-day', reason: null }

  while (departure < MINUTES_PER_DAY) {
    const time = formatTime(departure)

    let options: LegOptionsResponse
    try {
      // eslint-disable-next-line no-await-in-loop
      options = await deps.fetchLegOptions(day, time, aircraft.fleetAircraftId, draftWeekToInput(week))
    } catch {
      // A single round trip through the network failing must not sink the legs this day already
      // has, nor the rest of the week - same resilience the per-day generator before it had.
      stop = { kind: 'network', reason: null }
      break
    }

    known = limitsFrom(options, known)

    const pick = last === null ? legalOptionOf(options.legal, known) : findReturnOption(options.legal, last)
    if (!pick) {
      stop = describeRefusal(options, last, routesFlownToday)
      break
    }

    // Never a second airframe, however tempting: see this module's own doc. `aircraft` is fixed for
    // the whole call, so every leg placed below is on the same one.

    const blockMinutes = pick.blockMinutes ?? 60
    week = addLegToDay(week, day, draftLegFromOption(pick, time, blockMinutes))
    placed.push({ day, option: pick, time, blockMinutes })
    routesFlownToday.add(pick.routeId)
    last = pick

    // The turnaround is chosen HERE, before the next candidate is ever put to the backend - so the
    // time that gets validated is the time that gets proposed, and there is no window in which the
    // day was packed assuming one turn and saved with another. A turn that falls long simply means
    // the backend is asked about a later slot, and the day ends one leg sooner if that no longer
    // fits.
    //
    // At least a minute, always: a configuration with a zero turnaround minimum and a route with a
    // zero block time would otherwise leave this loop asking about the same slot forever. Nothing
    // real produces either, which is exactly why it is worth costing one line rather than trusting.
    const turnaround = turnaroundMinutesFor(aircraft.fleetAircraftId, day, placed.length, known.minTurnaroundMinutes)
    departure += Math.max(1, blockMinutes + turnaround)
  }

  return placed.length === 0
    ? { week: weekSoFar, placed: [], stop, limits: known }
    : { week, placed, stop, limits: known }
}

/**
 * Why the backend would not extend the day, in its own words where it has any.
 *
 * Which of the refusals to quote matters: `illegal` covers every route the airline has, most of
 * which were never candidates for this slot and are refused for reasons that would confuse rather
 * than inform ("EGGD -> LEPA is beyond G-TEST's range" says nothing about why Monday stopped at
 * seven legs). So the sector actually wanted is looked for by name - the exact reverse of the leg
 * just flown, which is the only thing this generator ever proposes next.
 *
 * <b>Two different places hold that answer, and both have to be read.</b> A sector refused because
 * of something committed EARLIER round the cycle comes back in `illegal`. A sector refused because
 * of something committed LATER - most often another pilot flying this same airframe later in the
 * week - comes back in `legal` carrying an "alert" warning instead, because for the manual picker
 * that is a consequence to accept rather than a refusal (see GetLegOptionsAsync). This generator
 * declines those, so for IT they are a refusal, and quoting the alert is the only way the day says
 * "the aircraft was needed elsewhere" instead of the uselessly vague "nothing could continue the
 * day". That case is not an edge case: a generated week deliberately uses one airframe throughout,
 * so a contended airframe is a first-class reason a day stops short.
 *
 * The fallback for a server that sends no airports with its refusals is a route this day has
 * ALREADY flown: one the aircraft is demonstrably standing in the right place for and physically
 * able to operate, so its refusal is necessarily about the thing that actually changed - the duty
 * day, the turnaround, the rest. Failing all of them, nothing is quoted at all; a wrong reason is
 * worse than an honest "nothing on offer could continue the day".
 */
function describeRefusal(
  options: LegOptionsResponse,
  last: LegalLegOption | null,
  routesFlownToday: ReadonlySet<string>,
): DayStop {
  const reverses = (departureIcao: string | undefined, arrivalIcao: string | undefined) =>
    last !== null && departureIcao !== undefined && arrivalIcao !== undefined &&
    icaoEquals(departureIcao, last.arrivalIcao) && icaoEquals(arrivalIcao, last.departureIcao)

  // The sector wanted, offered but carrying an alert this generator will not pick past. Its
  // warnings are ordered with alerts first by the backend, so warnings[0] is the one that blocked.
  const blocked = options.legal.find((option) => reverses(option.departureIcao, option.arrivalIcao) && isBlockingWarning(option))
  if (blocked) {
    return { kind: 'rule', reason: blocked.warnings[0]!.message }
  }

  const wanted = options.illegal.find((option) => reverses(option.departureIcao, option.arrivalIcao))
  const reason = (wanted ?? options.illegal.find((option) => routesFlownToday.has(option.routeId)))?.reason ?? null
  return reason === null ? { kind: 'nowhere-to-go', reason: null } : { kind: 'rule', reason }
}

/**
 * Commits only the longest run of proposed legs that leaves the aircraft back where the week
 * started - and this single step is the entire reason a generated day is now allowed to end
 * somewhere other than its base.
 *
 * The rule it protects has not moved an inch. A saved week is a cycle, so the backend requires the
 * last leg round the loop to connect back to the first (`requireWeekClosure: true` in
 * PilotScheduleValidator); a week that does not close is refused outright the moment it is saved.
 * The generator used to guarantee that by fixing one base airport and closing every day on itself -
 * structurally safe, but it also forced every day to an even number of legs and threw away the odd
 * sector the duty day could still have held. Defect that rule was written for, 2026-08-13: days
 * built independently against whatever position the backend reported each morning produced a week
 * based at two different airports that was individually legal per day and refused at save.
 *
 * Cutting the chain to a closed prefix keeps that promise while letting the days chain across
 * airports the way the player's own hand-built weeks do. It is safe to cut from the END and only
 * from the end:
 *
 * - <b>Duty only shrinks.</b> Removing a day's last legs moves its duty END earlier and never its
 *   start, so a day that was inside the 13-hour maximum still is.
 * - <b>Rest only grows.</b> Rest is measured from a duty day's end to the next one's first
 *   departure; earlier ends and untouched 08:00 starts can only lengthen it. A whole day falling
 *   away merges two rest periods into a longer one.
 * - <b>Turnaround is untouched.</b> Every surviving consecutive pair is a pair that already existed.
 * - <b>The anchored leg survives.</b> The one leg the backend pins to the airframe's real position
 *   is the first, and the first is never cut.
 *
 * A chain with no closing prefix at all commits nothing, which is the same refusal the generator
 * has always made rather than propose a dangling one-way leg the save would reject.
 */
function commitClosedPrefix(
  placed: readonly PlacedLeg[],
  stops: ReadonlyMap<DayOfWeek, DayStop>,
  aircraft: { fleetAircraftId: string; registration: string },
  lead: LegalLegOption | null,
): StarterScheduleBuilt {
  const empty: StarterScheduleBuilt = { week: {}, legsAdded: 0, daysUsed: 0, blockMinutes: 0, reason: null, stop: null }
  if (placed.length === 0) return empty

  const base = placed[0]!.option.departureIcao
  // Two legs is the shortest week that can close (a leg whose arrival equals its own departure is
  // not a thing any route describes), so there is nothing to look for below it.
  let kept: readonly PlacedLeg[] = []
  for (let length = placed.length; length >= 2; length -= 1) {
    if (icaoEquals(placed[length - 1]!.option.arrivalIcao, base)) {
      kept = placed.slice(0, length)
      break
    }
  }

  if (kept.length === 0) return empty

  // A day that lost legs to the cut did not stop because a rule refused it - it stopped because the
  // week has to end where it began. Saying "duty hours ran out" about a day the generator itself
  // shortened would be the one dishonest sentence this whole feature could produce.
  const trimmed = new Map(stops)
  for (let index = kept.length; index < placed.length; index += 1) {
    trimmed.set(placed[index]!.day, { kind: 'week-closure', reason: null })
  }

  let week: DraftWeek = {}
  const legsByDay = new Map<DayOfWeek, number>()
  let blockMinutes = 0
  for (const leg of kept) {
    if (!week[leg.day]) {
      week = setDayAircraft(week, leg.day, aircraft.fleetAircraftId, aircraft.registration)
    }
    week = addLegToDay(week, leg.day, draftLegFromOption(leg.option, leg.time, leg.blockMinutes))
    legsByDay.set(leg.day, (legsByDay.get(leg.day) ?? 0) + 1)
    blockMinutes += leg.blockMinutes
  }

  return {
    week,
    legsAdded: kept.length,
    daysUsed: legsByDay.size,
    blockMinutes,
    reason: lead === null ? null : reasonFor(aircraft.registration, lead),
    stop: fullestDayStop(legsByDay, trimmed),
  }
}

/**
 * The stop worth reporting: the day that took the most legs, since that is the day genuinely up
 * against a limit. Ties go to whichever comes first in {@link STARTER_DAYS}, so two identical days
 * always resolve the same way and the whole module stays deterministic.
 *
 * A day trimmed by {@link commitClosedPrefix} is deliberately still eligible - what stopped it is
 * still true of the day the generator built, and the count reported is the count actually handed
 * back, so the two never disagree with what is on screen.
 */
function fullestDayStop(
  legsByDay: ReadonlyMap<DayOfWeek, number>,
  stops: ReadonlyMap<DayOfWeek, DayStop>,
): StarterScheduleStop | null {
  let best: StarterScheduleStop | null = null
  for (const day of STARTER_DAYS) {
    const legs = legsByDay.get(day)
    const stop = stops.get(day)
    if (legs === undefined || stop === undefined) continue
    if (best === null || legs > best.legs) {
      best = { day, legs, kind: stop.kind, reason: stop.reason }
    }
  }
  return best
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
 * A warning this generator must not pick past, as opposed to one it is about to resolve itself.
 *
 * `"alert"` is a genuine incompatibility with something already committed elsewhere - double-booking
 * the airframe, too little turnaround or rest, an over-long duty day. Nothing the generator does
 * next makes those untrue, so an option carrying one is never taken: a generated week has to be
 * immediately saveable, never a promise the player has to keep.
 *
 * `"info"` is the opposite: a continuity gap, meaning "this leaves the aircraft at X and a later leg
 * needs it back at Y - add the return after this one." That is not an unresolved promise here, it is
 * a description of the generator's very next action, since every leg it proposes is followed either
 * by the leg that reverses it or by the cut that removes it (see {@link commitClosedPrefix}).
 * Refusing those made Sunday impossible to generate at all: the week is a cycle and Sunday sorts
 * before Monday, so a Sunday outbound ALWAYS reads as stranding the aircraft away from Monday's
 * first leg until its own return leg exists a moment later.
 */
function isBlockingWarning(option: LegalLegOption): boolean {
  return option.warnings.some((warning) => warning.severity === 'alert')
}

/**
 * The best legal option to open the week with. Two tiers, in this order, and the order matters:
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
 * Only ever asked once per week, for the opening leg. Every leg after it is the reverse of the one
 * before (see {@link findReturnOption}) and needs no ranking - there is exactly one leg that
 * reverses a given sector.
 */
function legalOptionOf(options: LegalLegOption[], limits: StarterScheduleLimits): LegalLegOption | undefined {
  return mostValuableOf(options.filter((option) => option.warnings.length === 0), limits)
    ?? mostValuableOf(options.filter((option) => !isBlockingWarning(option)), limits)
}

/** Highest-scoring option in one tier, falling back to the first when none of them can be scored.
 *  Ties keep the earlier option, so the result is a total order and two identical runs always agree
 *  - the determinism the whole module is built on (see this module's own doc). */
function mostValuableOf(tier: LegalLegOption[], limits: StarterScheduleLimits): LegalLegOption | undefined {
  let best: LegalLegOption | undefined
  let bestScore: number | null = null
  for (const option of tier) {
    const score = scoreLegOption(option, limits)
    if (score === null) continue
    if (bestScore === null || score > bestScore) {
      best = option
      bestScore = score
    }
  }
  return best ?? tier[0]
}

/** The option that flies the exact reverse of `outbound` - the only shape this generator ever
 *  proposes after the week's opening leg, which is what keeps the whole week a shuttle on one pair
 *  and closure a question of parity rather than of graph search. */
function findReturnOption(options: LegalLegOption[], outbound: LegalLegOption): LegalLegOption | undefined {
  const reverses = (option: LegalLegOption) =>
    icaoEquals(option.departureIcao, outbound.arrivalIcao) && icaoEquals(option.arrivalIcao, outbound.departureIcao)
  return options.find((option) => reverses(option) && option.warnings.length === 0)
    ?? options.find((option) => reverses(option) && !isBlockingWarning(option))
}

/** ICAO comparison the same way the backend does it (OrdinalIgnoreCase) - see draftEntry.ts's own
 *  remark for why this is not assumed away. */
function icaoEquals(a: string, b: string): boolean {
  return a.toUpperCase() === b.toUpperCase()
}

function formatTime(totalMinutes: number): string {
  return minutesToTime(totalMinutes).slice(0, 5)
}
