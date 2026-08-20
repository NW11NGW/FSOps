import { describe, expect, it, vi } from 'vitest'

import type { DraftWeek } from './draftEntry'
import {
  buildStarterSchedule,
  rankAircraftForStarterSchedule,
  scoreLegOption,
  STARTER_DAYS,
  STARTER_TIME,
  type StarterScheduleDeps,
} from './starterSchedule'
import type {
  AircraftOptionsResponse,
  DayOfWeek,
  DutyDayInput,
  IllegalLegOption,
  LegalLegOption,
  LegOptionsResponse,
  SchedulingLimits,
} from '@/types/schedule'
import { DAY_LABELS, timeToMinutes } from '@/types/schedule'

const AIRCRAFT = { fleetAircraftId: 'ac-1', registration: 'G-TEST' }

/** The airline's routes, in the only shape this module reads. Every fixture below flies out of
 *  EGKK, which is also where the fixture aircraft sits - so "can this aircraft start a chain from
 *  where it is standing" is true by default and only becomes interesting where a test says so. */
const ROUTES = [{ departureIcao: 'EGKK' }, { departureIcao: 'EGPH' }]

/** What the backend sends with every leg-options answer, and therefore what the generator spaces
 *  departures and ranks routes against - the shipped `SchedulingConfig` defaults. */
const SCHEDULING: SchedulingLimits = { maxDutyHoursPerDay: 13, minRestHoursBetweenDutyDays: 10, minTurnaroundMinutes: 30 }

const MAX_DUTY_MINUTES = SCHEDULING.maxDutyHoursPerDay * 60
const DAY_START_MINUTES = timeToMinutes(`${STARTER_TIME}:00`)

function aircraftOptions(options: AircraftOptionsResponse['options']): AircraftOptionsResponse {
  return { options }
}

function legOption(overrides: Partial<LegalLegOption> & { routeId: string; departureIcao: string; arrivalIcao: string; blockMinutes: number }): LegalLegOption {
  return { flightNumber: null, warnings: [], ...overrides }
}

/**
 * A responder that answers the way the real backend does: a leg is legal only while the duty day it
 * would produce still fits inside `SchedulingConfig.MaxDutyHoursPerDay` (13 hours from the day's
 * first departure to this candidate's arrival), and is refused with the backend's own sentence - and
 * the backend's own airports - once it does not.
 *
 * <b>Repaired 2026-08-20, and the repair is the point.</b> These fixtures used to answer "legal" at
 * any hour of any day, which only ever LOOKED correct because the generator carried a four-leg
 * ceiling that stopped it long before the answer could matter. With that ceiling gone the fixture is
 * what decides where a day ends, so a fixture that never says no is not testing the generator at
 * all - it is testing that the ceiling exists. Every test below that cares how long a day gets now
 * drives this instead.
 */
function dutyLimited(options: LegalLegOption[], day: DayOfWeek, limits: SchedulingLimits = SCHEDULING) {
  const maxDutyMinutes = limits.maxDutyHoursPerDay * 60
  return (time: string): LegOptionsResponse => {
    const legal: LegalLegOption[] = []
    const illegal: IllegalLegOption[] = []
    for (const option of options) {
      const dutyMinutes = timeToMinutes(`${time}:00`) + (option.blockMinutes ?? 60) - DAY_START_MINUTES
      if (dutyMinutes <= maxDutyMinutes) {
        legal.push(option)
      } else {
        illegal.push({
          routeId: option.routeId,
          departureIcao: option.departureIcao,
          arrivalIcao: option.arrivalIcao,
          reason: `Duty on ${DAY_LABELS[day]} runs ${(dutyMinutes / 60).toFixed(1)} hours, above the ${limits.maxDutyHoursPerDay}-hour maximum duty day.`,
        })
      }
    }
    return { legal, illegal, scheduling: limits }
  }
}

/** The same duty-limited answer on every day of the week - the ordinary case, where an aircraft is
 *  free to fly the same pair all week. */
function everyDay(options: LegalLegOption[], limits: SchedulingLimits = SCHEDULING) {
  return Object.fromEntries(STARTER_DAYS.map((day) => [day, dutyLimited(options, day, limits)]))
}

/** A fake `fetchLegOptions` keyed by day. `undefined` for a day means "nothing legal ever, on this
 *  day". Note that what a day answers may legitimately depend on the TIME queried - that is how the
 *  13-hour duty day ends up deciding a day's length, exactly as it does against a real server. */
function fakeDeps(byDay: Partial<Record<DayOfWeek, (time: string, draft: DutyDayInput[] | null) => LegOptionsResponse>>, aircraft = aircraftOptions([{ ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: true, reason: null, scheduledLegsThisWeek: 0 }])): StarterScheduleDeps {
  return {
    fetchAircraftOptions: vi.fn().mockResolvedValue(aircraft),
    fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string, _fleetAircraftId: string, draft: DutyDayInput[] | null) => {
      const responder = byDay[day]
      return responder ? responder(time, draft) : { legal: [], illegal: [] }
    }),
  }
}

/** Strips the client-generated `id` (fresh per call via nextDraftId, never equal across two
 *  separate runs) so structural/determinism comparisons focus on what actually describes the
 *  schedule: day, aircraft, time, route. */
function stripIds(week: ReturnType<typeof JSON.parse>) {
  return JSON.parse(
    JSON.stringify(week, (key, value) => (key === 'id' ? undefined : value)),
  )
}

/** Every leg of the week in the order the aircraft actually flies them - which is the order the
 *  generator builds them in, Monday through Sunday (see STARTER_DAYS). */
function chainOf(week: DraftWeek) {
  return STARTER_DAYS.flatMap((day) => (week[day]?.legs ?? []).map((leg) => ({ day, leg })))
}

/**
 * The two structural promises a generated week makes, asserted together because either alone is
 * worthless: every consecutive leg departs where the last one landed, AND the last one lands back
 * where the first departed.
 *
 * Deliberately NOT a copy of the backend's rules - it does not check duty, rest or turnaround, which
 * belong to PilotScheduleValidator and are proven against the real thing rather than against a
 * second implementation living here. What it checks is the shape this module is responsible for
 * producing, and specifically the shape that lets a day end away from its base at all (see
 * `commitClosedPrefix`): without closure, a week of individually-legal days is refused the moment it
 * is saved.
 */
function expectClosedChain(week: DraftWeek) {
  const chain = chainOf(week)
  expect(chain.length).toBeGreaterThanOrEqual(2)
  for (let i = 1; i < chain.length; i += 1) {
    expect(chain[i]!.leg.departureIcao).toBe(chain[i - 1]!.leg.arrivalIcao)
  }
  expect(chain[chain.length - 1]!.leg.arrivalIcao).toBe(chain[0]!.leg.departureIcao)
}

/** Every gap this week leaves between one leg landing and the next departing on the SAME day, in
 *  minutes. Cross-day gaps are rest, not turnaround, and are a different rule. */
function turnaroundsWithin(week: DraftWeek): number[] {
  const gaps: number[] = []
  for (const day of STARTER_DAYS) {
    const legs = week[day]?.legs ?? []
    for (let i = 1; i < legs.length; i += 1) {
      const previous = legs[i - 1]!
      gaps.push(timeToMinutes(legs[i]!.departureTimeUtc) - (timeToMinutes(previous.departureTimeUtc) + previous.blockMinutes))
    }
  }
  return gaps
}

describe('buildStarterSchedule - preconditions checked before generation', () => {
  it('reports no-routes and never even asks about aircraft when the airline has no routes', async () => {
    const deps = fakeDeps({})
    const outcome = await buildStarterSchedule([], deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-routes' } })
    expect(deps.fetchAircraftOptions).not.toHaveBeenCalled()
  })

  it('reports no-aircraft when the fleet is empty', async () => {
    const deps = fakeDeps({}, aircraftOptions([]))
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-aircraft' } })
  })

  it('reports all-reserved when every aircraft is reserved for the player, specifically (not the generic no-usable-aircraft)', async () => {
    const deps = fakeDeps(
      {},
      aircraftOptions([
        { ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: false, reason: 'G-TEST is reserved for the player - release it on the Fleet page to schedule it here.', scheduledLegsThisWeek: 0 },
        { fleetAircraftId: 'ac-2', registration: 'G-TEST2', aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: false, reason: 'G-TEST2 is reserved for the player - release it on the Fleet page to schedule it here.', scheduledLegsThisWeek: 0 },
      ]),
    )
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'all-reserved' } })
  })

  it('reports the generic no-usable-aircraft when ineligibility is not uniformly reservation (e.g. maintenance)', async () => {
    const deps = fakeDeps(
      {},
      aircraftOptions([
        { ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: false, reason: 'G-TEST is in maintenance until 2026-08-20 09:00 UTC.', scheduledLegsThisWeek: 0 },
      ]),
    )
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-usable-aircraft' } })
  })

  it('reports check-failed rather than throwing when the aircraft-options call itself fails', async () => {
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockRejectedValue(new Error('network down')),
      fetchLegOptions: vi.fn(),
    }
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'check-failed' } })
  })

  it('reports no-legal-schedule (the one generic case) when routes and aircraft both exist but nothing ever comes back legal', async () => {
    const deps = fakeDeps({
      1: () => ({ legal: [], illegal: [{ routeId: 'r1', reason: 'out of range' }] }),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-legal-schedule' } })
  })
})

// ---- User's complaint, 2026-08-20: "it's leaving valuable hours of flying on the table where the
// ---- pilot and airframe could be in the air". The generator used to stop at four legs a day while
// ---- the player was hand-building eight and nine through the picker, under the same rules. ----

describe('buildStarterSchedule - a duty day is filled until a rule stops it, not until a leg count does', () => {
  // EGKK <-> EGPH, ~65 minutes gate-to-gate - the user's own example.
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  /** Monday only, so this suite is about the shape of ONE duty day. */
  function mondayOnly() {
    return fakeDeps({ 1: dutyLimited([outbound, back], 1) })
  }

  it('fills far past the four legs it used to stop at, and every leg is one the backend called legal', async () => {
    const outcome = await buildStarterSchedule(ROUTES, mondayOnly())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    const legs = outcome.result.week[1]?.legs ?? []
    expect(legs.length).toBeGreaterThan(4)

    // The exact week, because the turnaround varies and this is what proves it varies
    // REPRODUCIBLY: 08:00 first, then block time plus a turnaround drawn deterministically from the
    // airframe, the day and the leg's position in it.
    expect(legs.map((l) => l.departureTimeUtc)).toEqual([
      '08:00:00', '09:44:00', '11:29:00', '13:04:00', '14:40:00', '16:17:00', '17:55:00', '19:34:00',
    ])
    expect(legs.map((l) => `${l.departureIcao}-${l.arrivalIcao}`)).toEqual([
      'EGKK-EGPH', 'EGPH-EGKK', 'EGKK-EGPH', 'EGPH-EGKK', 'EGKK-EGPH', 'EGPH-EGKK', 'EGKK-EGPH', 'EGPH-EGKK',
    ])
  })

  it('stops because the duty day ran out, and says which rule that was', async () => {
    const outcome = await buildStarterSchedule(ROUTES, mondayOnly())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    // "It stopped at four" is not an answer. This is: the ninth leg would have departed 21:09 and
    // landed 22:14, a 14.2-hour duty day, and the backend refused it in those words.
    expect(outcome.result.stop).toEqual({
      day: 1,
      legs: 8,
      kind: 'rule',
      reason: 'Duty on Monday runs 14.2 hours, above the 13-hour maximum duty day.',
    })
  })

  it('never proposes a duty day longer than the maximum it was told about', async () => {
    const outcome = await buildStarterSchedule(ROUTES, mondayOnly())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    const legs = outcome.result.week[1]!.legs
    const last = legs[legs.length - 1]!
    const dutyMinutes = timeToMinutes(last.departureTimeUtc) + last.blockMinutes - timeToMinutes(legs[0]!.departureTimeUtc)
    expect(dutyMinutes).toBeLessThanOrEqual(MAX_DUTY_MINUTES)
  })

  it('terminates on a fixture that never refuses anything, stopping at the calendar day', async () => {
    // Replaces the old "never exceeds the four-leg cap" test. The property worth protecting was
    // never the number four - it was that this loop cannot run away when nothing ever says no. It
    // still cannot: every leg advances the clock by at least one turnaround, so the calendar day is
    // reached and named.
    const alwaysLegal: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(aircraftOptions([{ ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: true, reason: null, scheduledLegsThisWeek: 0 }])),
      fetchLegOptions: vi.fn(async (day: DayOfWeek) => (day === 1 ? { legal: [outbound, back], illegal: [], scheduling: SCHEDULING } : { legal: [], illegal: [] })),
    }

    const outcome = await buildStarterSchedule(ROUTES, alwaysLegal)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs).toHaveLength(10)
    expect(outcome.result.stop?.kind).toBe('calendar-day')
    const legs = outcome.result.week[1]!.legs
    expect(timeToMinutes(legs[legs.length - 1]!.departureTimeUtc)).toBeLessThan(24 * 60)
  })

  it('reports total block time, which is the unit the player actually asked about', async () => {
    const outcome = await buildStarterSchedule(ROUTES, mondayOnly())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.blockMinutes).toBe(8 * 65)
  })

  it('is deterministic: the same inputs produce the same schedule, varied turnarounds and all', async () => {
    const first = await buildStarterSchedule(ROUTES, mondayOnly())
    const second = await buildStarterSchedule(ROUTES, mondayOnly())
    expect(first.ok).toBe(true)
    expect(second.ok).toBe(true)
    if (!first.ok || !second.ok) return
    expect(stripIds(first.result.week)).toEqual(stripIds(second.result.week))
    expect(first.result.legsAdded).toBe(second.result.legsAdded)
    expect(first.result.daysUsed).toBe(second.result.daysUsed)
    expect(first.result.stop).toEqual(second.result.stop)
  })
})

// ---- User's decision, 2026-08-20: "make it random between 30 and 40 mins" ----

describe('buildStarterSchedule - the turnaround varies, but never below the enforced floor', () => {
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  it('leaves every turn inside 30-40 minutes with the shipped configuration', async () => {
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([outbound, back])))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    const gaps = turnaroundsWithin(outcome.result.week)
    expect(gaps.length).toBeGreaterThan(20)
    for (const gap of gaps) {
      expect(gap).toBeGreaterThanOrEqual(SCHEDULING.minTurnaroundMinutes)
      expect(gap).toBeLessThanOrEqual(SCHEDULING.minTurnaroundMinutes + 10)
    }
  })

  it('actually varies rather than stamping one figure on every turn', async () => {
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([outbound, back])))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(new Set(turnaroundsWithin(outcome.result.week)).size).toBeGreaterThan(1)
  })

  it('moves the whole range with the configured minimum rather than keeping a second copy of 30', async () => {
    // The floor is the backend's, not this module's. An airline validated against a 50-minute
    // minimum must never be offered a 30-minute turn - which is exactly what a hard-coded range
    // would do.
    const strict: SchedulingLimits = { ...SCHEDULING, minTurnaroundMinutes: 50 }
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([outbound, back], strict)))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    const gaps = turnaroundsWithin(outcome.result.week)
    expect(gaps.length).toBeGreaterThan(0)
    for (const gap of gaps) {
      expect(gap).toBeGreaterThanOrEqual(50)
      expect(gap).toBeLessThanOrEqual(60)
    }
  })
})

describe('buildStarterSchedule - long-haul sector: block time rules out a same-day return', () => {
  // EGLL <-> KMCO, 9 hours gate-to-gate (540 min) - the user's own transatlantic example. A same-day
  // return would only close well after midnight, an 18-hour-plus duty day, so the return has to land
  // on the next duty day.
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGLL', arrivalIcao: 'KMCO', blockMinutes: 540 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'KMCO', arrivalIcao: 'EGLL', blockMinutes: 540 })

  function longHaulDeps() {
    return fakeDeps(everyDay([outbound, back]))
  }

  it('reaches a completely different day length from a short sector - one leg, not eight', async () => {
    const outcome = await buildStarterSchedule(ROUTES, longHaulDeps())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    for (const day of STARTER_DAYS) {
      const legs = outcome.result.week[day]?.legs ?? []
      expect(legs.length).toBeLessThanOrEqual(1)
    }
    // Six days of it, closed: out on Monday, back on Tuesday, out again on Wednesday, and so on.
    // The seventh leg would strand the aircraft in Florida, so it is not proposed - see the closure
    // suite below.
    expect(outcome.result.legsAdded).toBe(6)
    expect(outcome.result.daysUsed).toBe(6)
    expectClosedChain(outcome.result.week)
  })

  it('chains across airports the way a hand-built week does - Monday ends away, Tuesday starts there', async () => {
    const outcome = await buildStarterSchedule(ROUTES, longHaulDeps())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    const monday = outcome.result.week[1]!.legs
    const tuesday = outcome.result.week[2]!.legs
    expect(monday[monday.length - 1]!.arrivalIcao).toBe('KMCO')
    expect(tuesday[0]!.departureIcao).toBe('KMCO')
    expect(monday[0]!.departureTimeUtc).toBe('08:00:00')
    expect(tuesday[0]!.departureTimeUtc).toBe('08:00:00')

    // Why that is safe without touching the rest rule: every day departs at 08:00 and no duty day
    // may exceed 13 hours, so a duty day cannot end later than 21:00 and the next morning always
    // clears the 10-hour minimum.
    const mondayDutyEnd = timeToMinutes(monday[monday.length - 1]!.departureTimeUtc) + monday[monday.length - 1]!.blockMinutes
    expect((24 * 60 - mondayDutyEnd + DAY_START_MINUTES) / 60).toBeGreaterThanOrEqual(10)
  })

  it('never proposes the outbound alone when no day can legally close it', async () => {
    const deps = fakeDeps({
      1: (time) => (time === `${STARTER_TIME}` ? { legal: [outbound], illegal: [], scheduling: SCHEDULING } : { legal: [], illegal: [] }),
      // No day offers a return - the whole chain must be abandoned rather than left dangling.
      2: () => ({ legal: [], illegal: [] }),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-legal-schedule' } })
  })

  it('is deterministic across two independent runs', async () => {
    const first = await buildStarterSchedule(ROUTES, longHaulDeps())
    const second = await buildStarterSchedule(ROUTES, longHaulDeps())
    expect(first.ok && second.ok).toBe(true)
    if (!first.ok || !second.ok) return
    expect(stripIds(first.result.week)).toEqual(stripIds(second.result.week))
  })
})

// ---- The rule that made days end on their own base is now enforced across the WEEK instead ----

describe('buildStarterSchedule - the week always closes on itself', () => {
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })
  /** Long enough that a duty day holds an ODD number of legs (five), which is what makes a week of
   *  it finish at the wrong airport and need cutting. */
  const twoHourOut = legOption({ routeId: 'r-long-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 120 })
  const twoHourBack = legOption({ routeId: 'r-long-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 120 })

  it('leaves the aircraft where it started, however many days and legs it ended up using', async () => {
    // Replaces "every generated day is a closed out-and-back". That WAS how closure was guaranteed,
    // and it cost the odd sector a duty day could still hold. The promise it existed to keep is the
    // one asserted here, and it is unchanged: a week that does not close is refused the moment it is
    // saved (PilotScheduleValidator, requireWeekClosure: true).
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([outbound, back])))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    expect(outcome.result.daysUsed).toBe(7)
    expectClosedChain(outcome.result.week)
  })

  it('cuts the odd leg the week cannot bring home', async () => {
    // A two-hour sector fits five legs into a 13-hour day, so seven days of it is 35 legs and the
    // aircraft finishes at the wrong airport. The last leg is dropped and Sunday goes home with
    // four. Every other day keeps all five: the cut only ever takes from the end.
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([twoHourOut, twoHourBack])))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    expect(outcome.result.legsAdded).toBe(34)
    expect(outcome.result.week[1]?.legs).toHaveLength(5)
    expect(outcome.result.week[0]?.legs).toHaveLength(4) // Sunday, cut from five
    expectClosedChain(outcome.result.week)

    // The fullest day is still a day that genuinely ran out of duty hours, and that is what is
    // reported - the cut took an hour off Sunday, it did not change why Monday ended.
    expect(outcome.result.stop?.day).toBe(1)
    expect(outcome.result.stop?.kind).toBe('rule')
  })

  it('calls the cut what it is - the week closing, never "duty hours ran out"', async () => {
    // The same sector on one day only, so the day that was cut IS the day reported. It had well
    // over an hour of duty left; what it ran out of was week. Saying otherwise would be the one
    // dishonest sentence this feature could produce.
    const outcome = await buildStarterSchedule(ROUTES, fakeDeps({ 1: dutyLimited([twoHourOut, twoHourBack], 1) }))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    expect(outcome.result.stop).toEqual({ day: 1, legs: 4, kind: 'week-closure', reason: null })
    expect(outcome.result.week[1]?.legs).toHaveLength(4)
    expectClosedChain(outcome.result.week)
  })

  it('skips a day that cannot pick up where the aircraft was left, rather than bolting it on', async () => {
    // Defect this behaviour was written for, 2026-08-13, restated for a chained week. Another
    // pilot's legs move the airframe, so the backend legitimately offers different departures on
    // different mornings - and a day that can only ever leave from somewhere the chain is not is a
    // day that cannot join up. It is left empty; the week still closes without it.
    const fromLfpg = legOption({ routeId: 'r-lfpg-out', departureIcao: 'LFPG', arrivalIcao: 'EGKK', blockMinutes: 70 })

    const outcome = await buildStarterSchedule(ROUTES, fakeDeps({
      1: dutyLimited([outbound, back], 1),
      0: dutyLimited([fromLfpg], 0),
    }))

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs.length).toBeGreaterThan(0)
    expect(outcome.result.week[0]).toBeUndefined()
    expectClosedChain(outcome.result.week)
  })

  it('falls back to the best legal week rather than failing when the weekend will not fit', async () => {
    const deps = fakeDeps({
      1: dutyLimited([outbound, back], 1),
      2: dutyLimited([outbound, back], 2),
      3: dutyLimited([outbound, back], 3),
      4: dutyLimited([outbound, back], 4),
      5: dutyLimited([outbound, back], 5),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(5)
    expect(outcome.result.week[6]).toBeUndefined()
    expect(outcome.result.week[0]).toBeUndefined()
    expectClosedChain(outcome.result.week)
  })

  it('offers a weekend-only week when those are the only days that work', async () => {
    const deps = fakeDeps({
      6: dutyLimited([outbound, back], 6),
      0: dutyLimited([outbound, back], 0),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(2)
    expect(outcome.result.week[6]!.legs.length).toBeGreaterThan(4)
    expect(outcome.result.week[0]!.legs.length).toBeGreaterThan(4)
    expectClosedChain(outcome.result.week)
  })

  it('covers Monday through Sunday - Saturday and Sunday are days like any other', async () => {
    expect(STARTER_DAYS).toEqual([1, 2, 3, 4, 5, 6, 0])

    const outcome = await buildStarterSchedule(ROUTES, fakeDeps(everyDay([outbound, back])))
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(7)
    expect(outcome.result.week[6]?.legs.length).toBeGreaterThan(0) // Saturday
    expect(outcome.result.week[0]?.legs.length).toBeGreaterThan(0) // Sunday
  })
})

// ---- Real-use defect, 2026-08-13: only ever trying one aircraft ----

describe('buildStarterSchedule - more than one aircraft is tried', () => {
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  function option(id: string, registration: string, locationIcao: string, scheduledLegsThisWeek = 0) {
    return { fleetAircraftId: id, registration, aircraftTypeName: 'ATR 72-600', locationIcao, eligible: true, reason: null, scheduledLegsThisWeek }
  }

  it('moves on to the next eligible aircraft when the first one yields nothing', async () => {
    // The exact shape from real use: two identical ATRs, the busy one listed first by the server
    // (it was acquired first, and GetAircraftOptionsAsync orders by CreatedUtc). Only the second
    // can fly anything. Before this fix the generator stopped at the first and reported that no
    // legal starter schedule existed at all.
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(
        aircraftOptions([option('busy', 'G-NZHG', 'EGKK', 20), option('idle', 'G-FWLY', 'EGKK', 0)]),
      ),
      fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string, fleetAircraftId: string) => {
        if (fleetAircraftId !== 'idle' || day !== 1) return { legal: [], illegal: [] }
        return dutyLimited([outbound, back], 1)(time)
      }),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.registration).toBe('G-FWLY')
    expect(outcome.result.legsAdded).toBeGreaterThan(0)
    expectClosedChain(outcome.result.week)
  })

  it('still reports no-legal-schedule only once EVERY eligible aircraft has been tried', async () => {
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(
        aircraftOptions([option('a', 'G-AAAA', 'EGKK'), option('b', 'G-BBBB', 'EGKK'), option('c', 'G-CCCC', 'EGKK')]),
      ),
      fetchLegOptions: vi.fn(async () => ({ legal: [], illegal: [] })),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-legal-schedule' } })
    const asked = new Set((deps.fetchLegOptions as ReturnType<typeof vi.fn>).mock.calls.map((call) => call[2]))
    expect(asked).toEqual(new Set(['a', 'b', 'c']))
  })

  it('stops at the first aircraft that works rather than exhausting the fleet', async () => {
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(
        aircraftOptions([option('first', 'G-AAAA', 'EGKK'), option('second', 'G-BBBB', 'EGKK')]),
      ),
      fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string) => (day === 1 ? dutyLimited([outbound, back], 1)(time) : { legal: [], illegal: [] })),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    const asked = new Set((deps.fetchLegOptions as ReturnType<typeof vi.fn>).mock.calls.map((call) => call[2]))
    expect(asked).toEqual(new Set(['first']))
  })

  it('never mixes two airframes into one suggested week, even when the chosen one runs dry', async () => {
    // One pilot, one aircraft - the generator's own discipline (user's decision, 2026-08-20), not a
    // rule the app enforces: hand-building may still mix, and PilotScheduleValidator only ever
    // required one aircraft per DUTY DAY. Here the chosen airframe can only fly Monday while a
    // second could fly the rest of the week. Reaching for it would be a tempting way to squeeze out
    // more sectors, and it must not happen.
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(
        aircraftOptions([option('chosen', 'G-AAAA', 'EGKK', 0), option('other', 'G-BBBB', 'EGKK', 5)]),
      ),
      fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string, fleetAircraftId: string) => {
        if (fleetAircraftId === 'chosen' && day === 1) return dutyLimited([outbound, back], 1)(time)
        if (fleetAircraftId === 'other' && day !== 1) return dutyLimited([outbound, back], day)(time)
        return { legal: [], illegal: [] }
      }),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    const registrations = new Set(Object.values(outcome.result.week).map((day) => day!.registration))
    expect(registrations).toEqual(new Set(['G-AAAA']))
    expect(new Set(Object.values(outcome.result.week).map((day) => day!.fleetAircraftId))).toEqual(new Set(['chosen']))
    expect(outcome.result.daysUsed).toBe(1)
  })

  it('says the aircraft was needed elsewhere when that is what ended the day', async () => {
    // The airframe is contended by another pilot LATER in the week, so the backend returns the
    // sector as legal-with-an-alert rather than illegal (see GetLegOptionsAsync: a conflict against
    // something committed later is a consequence for the manual picker, not a refusal). This
    // generator declines it, so for IT it is a refusal - and the day has to say so in those words
    // rather than the uselessly vague "nothing could continue the day". Under one-pilot-one-aircraft
    // this is a first-class reason a day stops, not an edge case.
    const contendedFrom = 11 * 60
    const alert = { message: 'G-TEST is already flying EGKK -> EGPH at this time for another pilot.', severity: 'alert' as const }
    const deps = fakeDeps({
      1: (time) => ({
        legal: timeToMinutes(`${time}:00`) < contendedFrom ? [outbound, back] : [{ ...outbound, warnings: [alert] }, back],
        illegal: [],
        scheduling: SCHEDULING,
      }),
    })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    // The out-and-back it managed before the airframe was wanted elsewhere is kept and closed...
    expect(outcome.result.week[1]?.legs).toHaveLength(2)
    expectClosedChain(outcome.result.week)
    // ...and the day says why it went no further, naming the airframe and the other pilot.
    expect(outcome.result.stop).toEqual({ day: 1, legs: 2, kind: 'rule', reason: alert.message })
  })

  it('gives two aircraft different turnarounds, so two pilots do not fly identically-stamped days', async () => {
    const forAircraft = async (id: string) => {
      const deps: StarterScheduleDeps = {
        fetchAircraftOptions: vi.fn().mockResolvedValue(aircraftOptions([option(id, 'G-TEST', 'EGKK')])),
        fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string) => (day === 1 ? dutyLimited([outbound, back], 1)(time) : { legal: [], illegal: [] })),
      }
      const outcome = await buildStarterSchedule(ROUTES, deps)
      return outcome.ok ? turnaroundsWithin(outcome.result.week) : []
    }

    expect(await forAircraft('ac-alpha')).not.toEqual(await forAircraft('ac-beta'))
  })
})

describe('rankAircraftForStarterSchedule', () => {
  function option(registration: string, locationIcao: string, scheduledLegsThisWeek: number) {
    return { registration, locationIcao, scheduledLegsThisWeek }
  }

  it('puts an aircraft standing where a route departs ahead of one parked away from the network', async () => {
    // The user's own fleet: G-NZHG at LFPG, which no route departs from, listed first because it
    // was acquired first; G-FWLY at EGGD where every route begins.
    const ranked = rankAircraftForStarterSchedule(
      [option('G-NZHG', 'LFPG', 0), option('G-FWLY', 'EGGD', 0)],
      [{ departureIcao: 'EGGD' }, { departureIcao: 'EGPH' }],
    )
    expect(ranked.map((a) => a.registration)).toEqual(['G-FWLY', 'G-NZHG'])
  })

  it('prefers the least contended aircraft when both could start a chain', async () => {
    const ranked = rankAircraftForStarterSchedule(
      [option('G-BUSY', 'EGGD', 20), option('G-IDLE', 'EGGD', 0)],
      [{ departureIcao: 'EGGD' }],
    )
    expect(ranked.map((a) => a.registration)).toEqual(['G-IDLE', 'G-BUSY'])
  })

  it('falls back to the order the server gave, so two equally-good aircraft always resolve the same way', async () => {
    const ranked = rankAircraftForStarterSchedule(
      [option('G-ONE', 'EGGD', 4), option('G-TWO', 'EGGD', 4)],
      [{ departureIcao: 'EGGD' }],
    )
    expect(ranked.map((a) => a.registration)).toEqual(['G-ONE', 'G-TWO'])
  })

  it('compares ICAOs case-insensitively rather than silently ranking a lower-case location last', async () => {
    const ranked = rankAircraftForStarterSchedule(
      [option('G-AWAY', 'LFPG', 0), option('G-HOME', 'eggd', 0)],
      [{ departureIcao: 'EGGD' }],
    )
    expect(ranked.map((a) => a.registration)).toEqual(['G-HOME', 'G-AWAY'])
  })
})

describe('buildStarterSchedule - which warnings actually disqualify an option', () => {
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  it('takes an option carrying only a continuity gap - the very thing its next leg resolves', async () => {
    // Exactly what the backend returns for a Sunday outbound once the rest of the week exists:
    // legal, with an info-severity "add a EGPH -> EGKK leg after this one". Refusing it made Sunday
    // ungeneratable, because the week is a cycle and Sunday sorts before Monday.
    const gapWarned = legOption({
      routeId: 'r-out',
      departureIcao: 'EGKK',
      arrivalIcao: 'EGPH',
      blockMinutes: 65,
      warnings: [{ message: 'Leaves G-TEST at EGPH. Its next leg departs EGKK (Monday at 08:00) - add a EGPH -> EGKK leg after this one.', severity: 'info' }],
    })
    const deps = fakeDeps({ 1: dutyLimited([gapWarned, back], 1) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs.length).toBeGreaterThanOrEqual(2)
    expectClosedChain(outcome.result.week)
  })

  it('never takes an option carrying an alert - a real incompatibility no later leg undoes', async () => {
    const doubleBooked = legOption({
      routeId: 'r-out',
      departureIcao: 'EGKK',
      arrivalIcao: 'EGPH',
      blockMinutes: 65,
      warnings: [{ message: 'G-TEST is already flying EGKK -> EGPH at this time for another pilot.', severity: 'alert' }],
    })
    const deps = fakeDeps({ 1: dutyLimited([doubleBooked, back], 1) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-legal-schedule' } })
  })

  it('still prefers a completely clean option when one is on offer', async () => {
    const gapWarned = legOption({
      routeId: 'r-warned',
      departureIcao: 'EGKK',
      arrivalIcao: 'EIDW',
      blockMinutes: 55,
      warnings: [{ message: 'Leaves G-TEST at EIDW.', severity: 'info' }],
    })
    const deps = fakeDeps({ 1: dutyLimited([gapWarned, outbound, back], 1) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-out')
  })
})

// ---- User's decision, 2026-08-14: suggest the most profitable week for THAT aircraft and pilot,
// ---- not the same merely-legal route for everybody ----

describe('scoreLegOption - what a legal option is actually worth', () => {
  function priced(overrides: Partial<LegalLegOption> & { blockMinutes: number }): LegalLegOption {
    return legOption({ routeId: 'r', departureIcao: 'EGKK', arrivalIcao: 'EGPH', ...overrides })
  }

  it('cannot score an option the backend did not price, and says so rather than calling it worthless', () => {
    expect(scoreLegOption(priced({ blockMinutes: 65 }))).toBeNull()
    expect(scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: null }))).toBeNull()
  })

  it('values a short sector by the eight legs a day of it now carries', () => {
    // 65 min block, 35-minute typical turn: eight departures fit inside a 13-hour duty day. This
    // figure moved with the leg-count ceiling: it used to say four, because four was all the
    // generator would ever propose, which systematically under-rated short sectors.
    expect(scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: 800 }))).toBe(6400)
  })

  it('values a long-haul sector at the one leg a day it can actually fly', () => {
    // 540 min block: a second departure would land far outside the duty day, so a transatlantic is
    // one sector a day however handsome it is on its own.
    expect(scoreLegOption(priced({ blockMinutes: 540, expectedNetProfit: 2000 }))).toBe(2000)
  })

  it('prefers the week that fills the aircraft over the single most profitable sector', () => {
    const shortHop = scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: 800 }))
    const longHaul = scoreLegOption(priced({ blockMinutes: 540, expectedNetProfit: 2000 }))
    expect(shortHop).toBeGreaterThan(longHaul!)
  })

  it('does not credit a five-hour sector with legs the duty day cannot hold', () => {
    // 300 min block: a third departure at 19:10 still leaves before midnight, so "departs today"
    // alone would say three legs - but it would land after 00:00, a duty day the backend refuses.
    // Two is the honest figure, and getting this wrong systematically over-rates long sectors.
    expect(scoreLegOption(priced({ blockMinutes: 300, expectedNetProfit: 1000 }))).toBe(2000)
  })

  it('halves what a leg is worth once another pilot is already flying it - a shared market, not a ban', () => {
    const alone = scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: 800 }))
    const shared = scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: 800, scheduledLegsThisWeek: 1 }))
    expect(shared).toBe(alone! / 2)
  })

  it('ranks against the duty ceiling it was handed, not a hard-coded 13 hours', () => {
    // An airline configured with a shorter maximum duty day fits fewer sectors into it, and the
    // ranking has to know that or it will recommend a pattern the backend then refuses to build.
    const short = scoreLegOption(priced({ blockMinutes: 65, expectedNetProfit: 800 }), { maxDutyMinutes: 4 * 60, minTurnaroundMinutes: 30 })
    expect(short).toBe(2 * 800)
  })
})

describe('buildStarterSchedule - the route is chosen for what it earns', () => {
  // Two legs the aircraft could equally legally fly out of EGKK. The LESS profitable one is listed
  // first on purpose: before this change the generator took whatever the backend listed first, so a
  // test that passes only because of list order would prove nothing.
  const thinOut = legOption({ routeId: 'r-thin-out', departureIcao: 'EGKK', arrivalIcao: 'EIDW', blockMinutes: 65, expectedNetProfit: 300 })
  const thinBack = legOption({ routeId: 'r-thin-back', departureIcao: 'EIDW', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 290 })
  const richOut = legOption({ routeId: 'r-rich-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65, expectedNetProfit: 1200 })
  const richBack = legOption({ routeId: 'r-rich-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 1180 })

  /** Both pairs, both directions, at every slot of every day - so nothing but value steers which
   *  one the opening leg takes, and the chain can carry on from either airport it lands at. */
  function twoRoutesDeps(aircraft?: AircraftOptionsResponse) {
    return fakeDeps(everyDay([thinOut, thinBack, richOut, richBack]), aircraft)
  }

  it('takes the most profitable legal leg, not the first one offered, and fills the week with it', async () => {
    const outcome = await buildStarterSchedule(ROUTES, twoRoutesDeps())

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-rich-out')
    expect(outcome.result.week[1]?.legs.length).toBeGreaterThan(4)
    expect(outcome.result.daysUsed).toBe(7)
    expectClosedChain(outcome.result.week)
  })

  it('explains itself: the reason names the aircraft, the leg and what a sector of it earns', async () => {
    const outcome = await buildStarterSchedule(ROUTES, twoRoutesDeps())

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.reason).toEqual({
      registration: 'G-TEST',
      departureIcao: 'EGKK',
      arrivalIcao: 'EGPH',
      profitPerSector: 1200,
      otherPilotLegs: 0,
    })
  })

  it('leaves the reason unset rather than inventing one when the backend priced nothing', async () => {
    const unpricedOut = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
    const unpricedBack = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })
    const deps = fakeDeps({ 1: dutyLimited([unpricedOut, unpricedBack], 1) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.reason).toBeNull()
    // ...and the schedule itself is exactly what it always was: the first legal option.
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-out')
  })

  it('is still deterministic: the same airline in the same state suggests the same week twice', async () => {
    const first = await buildStarterSchedule(ROUTES, twoRoutesDeps())
    const second = await buildStarterSchedule(ROUTES, twoRoutesDeps())
    expect(first.ok && second.ok).toBe(true)
    if (!first.ok || !second.ok) return
    expect(stripIds(first.result.week)).toEqual(stripIds(second.result.week))
    expect(first.result.reason).toEqual(second.result.reason)
  })

  it('never lets a profitable option carrying an alert past a clean one - ranking is among legal options only', async () => {
    // The richest leg on offer is double-booked. Profit must not buy it a way past a warning the
    // generator's own next action cannot resolve; the thin-but-clean pair is the right answer.
    const richButBlocked = legOption({
      routeId: 'r-rich-out',
      departureIcao: 'EGKK',
      arrivalIcao: 'EGPH',
      blockMinutes: 65,
      expectedNetProfit: 99_000,
      warnings: [{ message: 'G-TEST is already flying EGKK -> EGPH at this time for another pilot.', severity: 'alert' }],
    })
    const deps = fakeDeps({ 1: dutyLimited([richButBlocked, thinOut, thinBack], 1) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-thin-out')
  })
})

describe('buildStarterSchedule - two pilots do not get the same week', () => {
  const richOut = legOption({ routeId: 'r-rich-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65, expectedNetProfit: 1000 })
  const richBack = legOption({ routeId: 'r-rich-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 980 })
  const otherOut = legOption({ routeId: 'r-other-out', departureIcao: 'EGKK', arrivalIcao: 'EIDW', blockMinutes: 65, expectedNetProfit: 700 })
  const otherBack = legOption({ routeId: 'r-other-back', departureIcao: 'EIDW', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 690 })

  /** `contendedLegs` is what the backend reports for the rich pair once the FIRST pilot's week has
   *  been saved on it - the second pilot is asking the same question against a different world. A
   *  contended city pair is contended in BOTH directions, which is how the backend counts it. */
  function depsFor(contendedLegs: number) {
    return fakeDeps(everyDay([
      { ...richOut, scheduledLegsThisWeek: contendedLegs },
      { ...richBack, scheduledLegsThisWeek: contendedLegs },
      otherOut,
      otherBack,
    ]))
  }

  it('hands the second pilot a different pair once the first is working the best one', async () => {
    const firstPilot = await buildStarterSchedule(ROUTES, depsFor(0))
    // Eight legs a day, seven days: the rich pair now carries 56 of the first pilot's legs.
    const secondPilot = await buildStarterSchedule(ROUTES, depsFor(56))

    expect(firstPilot.ok && secondPilot.ok).toBe(true)
    if (!firstPilot.ok || !secondPilot.ok) return

    expect(firstPilot.result.week[1]?.legs[0]?.routeId).toBe('r-rich-out')
    expect(secondPilot.result.week[1]?.legs[0]?.routeId).toBe('r-other-out')
    expect(secondPilot.result.week[1]?.legs[0]?.routeId).not.toBe(firstPilot.result.week[1]?.legs[0]?.routeId)
  })

  it('still gives the second pilot the contended pair when it is genuinely the only legal one', async () => {
    // The fall-back the design asks for: sharing a market is a reason to prefer something else, not
    // a reason to hand back an empty week when there IS nothing else.
    const deps = fakeDeps(everyDay([
      { ...richOut, scheduledLegsThisWeek: 56 },
      { ...richBack, scheduledLegsThisWeek: 56 },
    ]))

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-rich-out')
    expect(outcome.result.legsAdded).toBeGreaterThan(0)
    expect(outcome.result.reason?.otherPilotLegs).toBe(56)
    expectClosedChain(outcome.result.week)
  })

  it('gives the same aircraft type a different answer at a different base', async () => {
    // Two identical airframes, one at EGKK and one at EGPH, each with its own market on its
    // doorstep. Whichever one the aircraft ranking reaches for, the ROUTE it gets is the best one
    // from where it is standing - "the most profitable route" is not a property of the route alone.
    const fromEgkk = legOption({ routeId: 'r-egkk-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65, expectedNetProfit: 900 })
    const toEgkk = legOption({ routeId: 'r-egkk-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 880 })
    const fromEgph = legOption({ routeId: 'r-egph-out', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65, expectedNetProfit: 880 })
    const toEgph = legOption({ routeId: 'r-egph-back', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65, expectedNetProfit: 900 })

    function depsForBase(location: 'EGKK' | 'EGPH'): StarterScheduleDeps {
      // Only a leg departing where the aircraft actually stands can lead the chain - the backend
      // would never offer the other direction at 08:00, so the fixture does not either.
      const outbound = location === 'EGKK' ? fromEgkk : fromEgph
      const inbound = location === 'EGKK' ? toEgkk : toEgph
      return {
        fetchAircraftOptions: vi.fn().mockResolvedValue(
          aircraftOptions([{ fleetAircraftId: 'ac-1', registration: 'G-TWIN', aircraftTypeName: 'ATR 72-600', locationIcao: location, eligible: true, reason: null, scheduledLegsThisWeek: 0 }]),
        ),
        fetchLegOptions: vi.fn(async (day: DayOfWeek, time: string) => {
          if (day !== 1) return { legal: [], illegal: [] }
          return time === STARTER_TIME ? dutyLimited([outbound], 1)(time) : dutyLimited([outbound, inbound], 1)(time)
        }),
      }
    }

    const atEgkk = await buildStarterSchedule(ROUTES, depsForBase('EGKK'))
    const atEgph = await buildStarterSchedule(ROUTES, depsForBase('EGPH'))

    expect(atEgkk.ok && atEgph.ok).toBe(true)
    if (!atEgkk.ok || !atEgph.ok) return
    expect(atEgkk.result.week[1]?.legs[0]?.routeId).toBe('r-egkk-out')
    expect(atEgph.result.week[1]?.legs[0]?.routeId).toBe('r-egph-out')
    expect(atEgkk.result.reason?.departureIcao).toBe('EGKK')
    expect(atEgph.result.reason?.departureIcao).toBe('EGPH')
  })
})
