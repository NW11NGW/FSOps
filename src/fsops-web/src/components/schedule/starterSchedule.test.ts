import { describe, expect, it, vi } from 'vitest'

import { buildStarterSchedule, rankAircraftForStarterSchedule, STARTER_DAYS, type StarterScheduleDeps } from './starterSchedule'
import type { AircraftOptionsResponse, DayOfWeek, DutyDayInput, LegalLegOption, LegOptionsResponse } from '@/types/schedule'

const AIRCRAFT = { fleetAircraftId: 'ac-1', registration: 'G-TEST' }

/** The airline's routes, in the only shape this module reads. Every fixture below flies out of
 *  EGKK, which is also where the fixture aircraft sits - so "can this aircraft start a chain from
 *  where it is standing" is true by default and only becomes interesting where a test says so. */
const ROUTES = [{ departureIcao: 'EGKK' }, { departureIcao: 'EGPH' }]

function aircraftOptions(options: AircraftOptionsResponse['options']): AircraftOptionsResponse {
  return { options }
}

function legOption(overrides: Partial<LegalLegOption> & { routeId: string; departureIcao: string; arrivalIcao: string; blockMinutes: number }): LegalLegOption {
  return { flightNumber: null, warnings: [], ...overrides }
}

/** A fake `fetchLegOptions` keyed purely by day - what each day answers is fixed for every time
 *  queried on it, which is all these tests need: the point under test is what buildStarterSchedule
 *  DOES with a given answer, not re-deriving what a real legality check would say (that is
 *  PilotScheduleValidator's own, already-covered, job). `undefined` for a day means "nothing legal
 *  ever, on this day". */
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

describe('buildStarterSchedule - short sector: block time leaves room for two round trips', () => {
  // EGKK <-> EGPH, ~65 minutes gate-to-gate - the user's own example. At 08:00, 65 min block and a
  // 45 min gap between legs, the round trip repeats at 09:50, 11:40, 13:30 - four legs finishing
  // duty at 14:35, comfortably inside the 13-hour cap, so the generator should take the full cap.
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  function shortSectorDeps() {
    return fakeDeps({
      1: () => ({ legal: [outbound, back], illegal: [] }),
    })
  }

  it('fills a generated day up to the four-leg cap when duty hours allow it, and it would be immediately saveable', async () => {
    const outcome = await buildStarterSchedule(ROUTES, shortSectorDeps())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    expect(outcome.result.daysUsed).toBe(1)
    expect(outcome.result.legsAdded).toBe(4)

    const day = outcome.result.week[1]
    expect(day?.legs).toHaveLength(4)
    expect(day?.legs.map((l) => l.departureTimeUtc)).toEqual(['08:00:00', '09:50:00', '11:40:00', '13:30:00'])
    expect(day?.legs.map((l) => `${l.departureIcao}-${l.arrivalIcao}`)).toEqual(['EGKK-EGPH', 'EGPH-EGKK', 'EGKK-EGPH', 'EGPH-EGKK'])

    // Total duty (first departure to last arrival) must stay inside the 13-hour maximum.
    const lastLeg = day!.legs[3]!
    const lastDeparture = 13 * 60 + 30
    const dutyEnd = lastDeparture + lastLeg.blockMinutes
    expect((dutyEnd - 8 * 60) / 60).toBeLessThanOrEqual(13)
  })

  it('never exceeds the four-leg generator cap even when every further round trip would still be legal', async () => {
    // A fake that is ALWAYS legal, at any time, on day 1 - if the cap were not enforced in code,
    // this would recurse indefinitely. It must stop at 4 regardless.
    const alwaysLegalDeps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockResolvedValue(aircraftOptions([{ ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: true, reason: null, scheduledLegsThisWeek: 0 }])),
      fetchLegOptions: vi.fn(async (day: DayOfWeek) => (day === 1 ? { legal: [outbound, back], illegal: [] } : { legal: [], illegal: [] })),
    }
    const outcome = await buildStarterSchedule(ROUTES, alwaysLegalDeps)
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs).toHaveLength(4)
  })

  it('is deterministic: the same inputs produce the same schedule (ignoring the client-only draft id)', async () => {
    const first = await buildStarterSchedule(ROUTES, shortSectorDeps())
    const second = await buildStarterSchedule(ROUTES, shortSectorDeps())
    expect(first.ok).toBe(true)
    expect(second.ok).toBe(true)
    if (!first.ok || !second.ok) return
    expect(stripIds(first.result.week)).toEqual(stripIds(second.result.week))
    expect(first.result.legsAdded).toBe(second.result.legsAdded)
    expect(first.result.daysUsed).toBe(second.result.daysUsed)
  })
})

describe('buildStarterSchedule - long-haul sector: block time rules out a same-day return', () => {
  // EGLL <-> KMCO, 9 hours gate-to-gate (540 min) - the user's own transatlantic example. A same-day
  // return at 08:00 + 9h + 45min = 17:45 would only close at 02:45 the FOLLOWING day, an 18h45m duty
  // day, so a real leg-options check would refuse it; the return has to land on the next duty day.
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGLL', arrivalIcao: 'KMCO', blockMinutes: 540 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'KMCO', arrivalIcao: 'EGLL', blockMinutes: 540 })

  function longHaulDeps() {
    return fakeDeps({
      1: (time) => {
        if (time === '08:00') return { legal: [outbound], illegal: [] }
        // 17:45 - the same-day return attempt. A real duty-hour check would refuse this (18h45m
        // duty), so the fake mirrors that refusal rather than the generator's own guess.
        return { legal: [], illegal: [{ routeId: 'r-back', reason: 'Duty on Monday runs 18.8 hours, above the 13-hour maximum duty day.' }] }
      },
      2: (time) => (time === '08:00' ? { legal: [back], illegal: [] } : { legal: [], illegal: [] }),
    })
  }

  it('proposes exactly one leg on the first day, not four, closed by the return on the next day', async () => {
    const outcome = await buildStarterSchedule(ROUTES, longHaulDeps())
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return

    expect(outcome.result.legsAdded).toBe(2)
    expect(outcome.result.daysUsed).toBe(2)

    const monday = outcome.result.week[1]
    const tuesday = outcome.result.week[2]
    expect(monday?.legs).toHaveLength(1)
    expect(tuesday?.legs).toHaveLength(1)

    const mondayLeg = monday!.legs[0]!
    const tuesdayLeg = tuesday!.legs[0]!

    expect(mondayLeg.departureIcao).toBe('EGLL')
    expect(mondayLeg.arrivalIcao).toBe('KMCO')
    expect(mondayLeg.departureTimeUtc).toBe('08:00:00')

    expect(tuesdayLeg.departureIcao).toBe('KMCO')
    expect(tuesdayLeg.arrivalIcao).toBe('EGLL')
    expect(tuesdayLeg.departureTimeUtc).toBe('08:00:00')

    // Physically possible: rest between Monday's duty end (08:00 + 9h = 17:00) and Tuesday's
    // 08:00 departure is 15 hours, clearing the 10-hour minimum with room to spare.
    const mondayDutyEndMinutes = 8 * 60 + mondayLeg.blockMinutes
    const restHours = (24 * 60 - mondayDutyEndMinutes + 8 * 60) / 60
    expect(restHours).toBeGreaterThanOrEqual(10)
  })

  it('never proposes the outbound alone when no day can legally close it', async () => {
    const deps = fakeDeps({
      1: (time) => (time === '08:00' ? { legal: [outbound], illegal: [] } : { legal: [], illegal: [] }),
      // Tuesday offers no legal return either - the whole Monday/Tuesday chain must be abandoned.
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
      fetchLegOptions: vi.fn(async (day: DayOfWeek, _time: string, fleetAircraftId: string) => {
        if (fleetAircraftId !== 'idle' || day !== 1) return { legal: [], illegal: [] }
        return { legal: [outbound, back], illegal: [] }
      }),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.registration).toBe('G-FWLY')
    expect(outcome.result.legsAdded).toBeGreaterThan(0)
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
      fetchLegOptions: vi.fn(async (day: DayOfWeek) => (day === 1 ? { legal: [outbound, back], illegal: [] } : { legal: [], illegal: [] })),
    }

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    const asked = new Set((deps.fetchLegOptions as ReturnType<typeof vi.fn>).mock.calls.map((call) => call[2]))
    expect(asked).toEqual(new Set(['first']))
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
    const deps = fakeDeps({ 1: () => ({ legal: [gapWarned, back], illegal: [] }) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs.length).toBeGreaterThanOrEqual(2)
  })

  it('never takes an option carrying an alert - a real incompatibility no later leg undoes', async () => {
    const doubleBooked = legOption({
      routeId: 'r-out',
      departureIcao: 'EGKK',
      arrivalIcao: 'EGPH',
      blockMinutes: 65,
      warnings: [{ message: 'G-TEST is already flying EGKK -> EGPH at this time for another pilot.', severity: 'alert' }],
    })
    const deps = fakeDeps({ 1: () => ({ legal: [doubleBooked, back], illegal: [] }) })

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
    const deps = fakeDeps({ 1: () => ({ legal: [gapWarned, outbound, back], illegal: [] }) })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs[0]?.routeId).toBe('r-out')
  })
})

// ---- User's decision, 2026-08-13: the starter schedule covers the weekend too ----

describe('buildStarterSchedule - the whole week, weekend included', () => {
  const outbound = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
  const back = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })

  it('covers Monday through Sunday - Saturday and Sunday are days like any other', async () => {
    expect(STARTER_DAYS).toEqual([1, 2, 3, 4, 5, 6, 0])

    const deps = fakeDeps(
      Object.fromEntries(STARTER_DAYS.map((day) => [day, () => ({ legal: [outbound, back], illegal: [] })])),
    )
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(7)
    expect(outcome.result.week[6]?.legs.length).toBeGreaterThan(0) // Saturday
    expect(outcome.result.week[0]?.legs.length).toBeGreaterThan(0) // Sunday
  })

  it('every generated day is a closed out-and-back, so seven of them still leave the week closed', async () => {
    // Nothing about adding the weekend needs a relaxed rule, and this is why: each day starts and
    // ends at the same airport, so the aircraft is back where it began every night AND at the end
    // of the week - which is exactly what the backend's wrap/closure check requires of a saved week.
    const deps = fakeDeps(
      Object.fromEntries(STARTER_DAYS.map((day) => [day, () => ({ legal: [outbound, back], illegal: [] })])),
    )
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    for (const day of STARTER_DAYS) {
      const legs = outcome.result.week[day]?.legs ?? []
      expect(legs.length).toBeGreaterThan(0)
      expect(legs[0]!.departureIcao).toBe(legs[legs.length - 1]!.arrivalIcao)
    }
  })

  it('falls back to the best legal week rather than failing when the weekend will not fit', async () => {
    // Saturday and Sunday come back with nothing legal - a real refusal the generator must respect
    // rather than work around. The five weekdays that DO fit are still handed back.
    const deps = fakeDeps({
      1: () => ({ legal: [outbound, back], illegal: [] }),
      2: () => ({ legal: [outbound, back], illegal: [] }),
      3: () => ({ legal: [outbound, back], illegal: [] }),
      4: () => ({ legal: [outbound, back], illegal: [] }),
      5: () => ({ legal: [outbound, back], illegal: [] }),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(5)
    expect(outcome.result.week[6]).toBeUndefined()
    expect(outcome.result.week[0]).toBeUndefined()
  })

  it('bases every day at the same airport, so a week built across days that see different aircraft positions still closes', async () => {
    // Defect caught in testing, 2026-08-13. Another pilot's legs earlier in the week move the
    // airframe, so the backend legitimately reports a DIFFERENT position for different mornings -
    // Saturday out of EGKK, Sunday out of LFPG. Building each day against its own position produced
    // individually-legal days that could not join up, and the generated week was refused the moment
    // it was saved. Whichever airport the first chain establishes, every later day must leave from
    // there or be skipped.
    const fromEgkk = legOption({ routeId: 'r-out', departureIcao: 'EGKK', arrivalIcao: 'EGPH', blockMinutes: 65 })
    const toEgkk = legOption({ routeId: 'r-back', departureIcao: 'EGPH', arrivalIcao: 'EGKK', blockMinutes: 65 })
    const fromLfpg = legOption({ routeId: 'r-lfpg-out', departureIcao: 'LFPG', arrivalIcao: 'EGKK', blockMinutes: 70 })

    const deps = fakeDeps({
      1: () => ({ legal: [fromEgkk, toEgkk], illegal: [] }),
      // Sunday can only ever depart LFPG - a different airport, so it must be skipped rather than
      // bolted onto a week that starts and ends at EGKK.
      0: () => ({ legal: [fromLfpg], illegal: [] }),
    })

    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs.length).toBeGreaterThan(0)
    expect(outcome.result.week[0]).toBeUndefined()

    // The property that actually matters: across the whole week, every day departs from the same
    // airport and returns to it, so the week closes on itself however many days it ended up using.
    const departures = new Set(Object.values(outcome.result.week).map((day) => day!.legs[0]!.departureIcao))
    expect(departures.size).toBe(1)
    for (const day of Object.values(outcome.result.week)) {
      expect(day!.legs[day!.legs.length - 1]!.arrivalIcao).toBe(day!.legs[0]!.departureIcao)
    }
  })

  it('offers a weekend-only week when those are the only days that work', async () => {
    const deps = fakeDeps({
      6: () => ({ legal: [outbound, back], illegal: [] }),
      0: () => ({ legal: [outbound, back], illegal: [] }),
    })
    const outcome = await buildStarterSchedule(ROUTES, deps)

    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.daysUsed).toBe(2)
    expect(outcome.result.week[6]?.legs).toHaveLength(4)
    expect(outcome.result.week[0]?.legs).toHaveLength(4)
  })
})
