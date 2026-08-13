import { describe, expect, it, vi } from 'vitest'

import { buildStarterSchedule, type StarterScheduleDeps } from './starterSchedule'
import type { AircraftOptionsResponse, DayOfWeek, DutyDayInput, LegalLegOption, LegOptionsResponse } from '@/types/schedule'

const AIRCRAFT = { fleetAircraftId: 'ac-1', registration: 'G-TEST' }

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
    const outcome = await buildStarterSchedule(0, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-routes' } })
    expect(deps.fetchAircraftOptions).not.toHaveBeenCalled()
  })

  it('reports no-aircraft when the fleet is empty', async () => {
    const deps = fakeDeps({}, aircraftOptions([]))
    const outcome = await buildStarterSchedule(1, deps)
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
    const outcome = await buildStarterSchedule(1, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'all-reserved' } })
  })

  it('reports the generic no-usable-aircraft when ineligibility is not uniformly reservation (e.g. maintenance)', async () => {
    const deps = fakeDeps(
      {},
      aircraftOptions([
        { ...AIRCRAFT, aircraftTypeName: 'Test', locationIcao: 'EGKK', eligible: false, reason: 'G-TEST is in maintenance until 2026-08-20 09:00 UTC.', scheduledLegsThisWeek: 0 },
      ]),
    )
    const outcome = await buildStarterSchedule(1, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-usable-aircraft' } })
  })

  it('reports check-failed rather than throwing when the aircraft-options call itself fails', async () => {
    const deps: StarterScheduleDeps = {
      fetchAircraftOptions: vi.fn().mockRejectedValue(new Error('network down')),
      fetchLegOptions: vi.fn(),
    }
    const outcome = await buildStarterSchedule(1, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'check-failed' } })
  })

  it('reports no-legal-schedule (the one generic case) when routes and aircraft both exist but nothing ever comes back legal', async () => {
    const deps = fakeDeps({
      1: () => ({ legal: [], illegal: [{ routeId: 'r1', reason: 'out of range' }] }),
    })
    const outcome = await buildStarterSchedule(1, deps)
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
    const outcome = await buildStarterSchedule(1, shortSectorDeps())
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
    const outcome = await buildStarterSchedule(1, alwaysLegalDeps)
    expect(outcome.ok).toBe(true)
    if (!outcome.ok) return
    expect(outcome.result.week[1]?.legs).toHaveLength(4)
  })

  it('is deterministic: the same inputs produce the same schedule (ignoring the client-only draft id)', async () => {
    const first = await buildStarterSchedule(1, shortSectorDeps())
    const second = await buildStarterSchedule(1, shortSectorDeps())
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
    const outcome = await buildStarterSchedule(1, longHaulDeps())
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
    const outcome = await buildStarterSchedule(1, deps)
    expect(outcome).toEqual({ ok: false, issue: { kind: 'no-legal-schedule' } })
  })

  it('is deterministic across two independent runs', async () => {
    const first = await buildStarterSchedule(1, longHaulDeps())
    const second = await buildStarterSchedule(1, longHaulDeps())
    expect(first.ok && second.ok).toBe(true)
    if (!first.ok || !second.ok) return
    expect(stripIds(first.result.week)).toEqual(stripIds(second.result.week))
  })
})
