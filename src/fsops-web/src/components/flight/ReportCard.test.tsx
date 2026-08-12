import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ReportCard } from './ReportCard'
import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { Flight, FlightDetail, FlightEvent, FlightLedgerLine, MismatchPayload } from '@/types/flight'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function flight(overrides: Partial<Flight> = {}): Flight {
  return {
    id: 'flight-1',
    airlineId: 'airline-1',
    routeId: 'route-1',
    fleetAircraftId: 'ac-1',
    pilotId: 'pilot-1',
    status: 'Completed',
    plannedDepartureUtc: '2026-08-11T09:00:00Z',
    plannedBlockMinutes: 75,
    outUtc: '2026-08-11T09:05:00Z',
    offUtc: '2026-08-11T09:15:00Z',
    onUtc: '2026-08-11T10:15:00Z',
    inUtc: '2026-08-11T10:20:00Z',
    paxBooked: 150,
    paxFlown: 148,
    fuelPlannedKg: 2200,
    fuelUsedKg: 2100,
    landingFpmFirst: -142,
    landingFpmHardest: -142,
    landingGForce: 1.24,
    centrelineDeviationM: 1.5,
    titleFlown: 'Airbus A320neo',
    typeMismatch: false,
    simRateElevated: false,
    maxSimulationRateObserved: 1,
    slewDetected: false,
    positionJumpDetected: false,
    revenue: 21000,
    totalCost: 13500,
    createdUtc: '2026-08-11T08:30:00Z',
    vatsimOnline: null,
    vatsimCallsign: null,
    vatsimOnlineFraction: null,
    vatsimControllersWorked: null,
    ...overrides,
  }
}

function touchdownEvent(id: string): FlightEvent {
  return { id, utc: '2026-08-11T10:15:00Z', type: 'Touchdown', payloadJson: '{}' }
}

function mismatchEvent(payload: MismatchPayload): FlightEvent {
  return { id: 'mismatch-1', utc: '2026-08-11T09:00:00Z', type: 'Mismatch', payloadJson: JSON.stringify(payload) }
}

function ledgerLine(overrides: Partial<FlightLedgerLine> = {}): FlightLedgerLine {
  return {
    id: 'ledger-1',
    utc: '2026-08-11T10:20:00Z',
    category: 'TicketRevenue',
    amount: 21000,
    description: 'Ticket revenue (148 passengers)',
    ...overrides,
  }
}

function detail(overrides: Partial<FlightDetail> = {}): FlightDetail {
  return {
    flight: overrides.flight ?? flight(),
    events: overrides.events ?? [touchdownEvent('td-1')],
    ledgerTransactions: overrides.ledgerTransactions ?? [
      ledgerLine({ id: 'l1', category: 'TicketRevenue', amount: 21000, description: 'Ticket revenue (148 passengers)' }),
      ledgerLine({ id: 'l2', category: 'Fuel', amount: -3500, description: 'Fuel: 2,100 kg burned' }),
    ],
    aircraftFuelOnBoardKg: overrides.aircraftFuelOnBoardKg === undefined ? 4200 : overrides.aircraftFuelOnBoardKg,
  }
}

const route = {
  departureIcao: 'EGGD',
  departureName: 'Bristol',
  arrivalIcao: 'EGPH',
  arrivalName: 'Edinburgh',
  flightNumber: '204',
}

async function render(props: Partial<Parameters<typeof ReportCard>[0]> = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <ReportCard
        detail={props.detail ?? detail()}
        route={props.route === undefined ? route : props.route}
        airlineIcaoCode={props.airlineIcaoCode === undefined ? 'FSO' : props.airlineIcaoCode}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('ReportCard - landing quality', () => {
  it('shows the touchdown rate and verdict when a landing was captured', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ landingFpmFirst: -142 }) }) })
    const body = text(container)
    expect(body).toContain('-142')
    expect(body).toContain('fpm')
    unmount()
  })

  it('says plainly that no touchdown was captured, without inventing a zero', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ landingFpmFirst: null }) }) })
    expect(text(container)).toContain('No touchdown was captured for this flight.')
    unmount()
  })

  it('counts bounces as one fewer than the number of Touchdown events', async () => {
    const { container, unmount } = await render({
      detail: detail({ events: [touchdownEvent('td-1'), touchdownEvent('td-2'), touchdownEvent('td-3')] }),
    })
    const body = text(container)
    expect(body).toContain('Bounces')
    expect(body).toMatch(/Bounces\s*2|2\s*Bounces/)
    unmount()
  })

  it('reports the hardest touchdown as a sink rate regardless of the sign it arrived with', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ landingFpmHardest: 210 }) }) })
    expect(text(container)).toContain('-210 fpm')
    unmount()
  })

  it('shows a placeholder for centreline deviation when it could not be determined', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ centrelineDeviationM: null }) }) })
    expect(text(container)).toContain('Centreline deviation')
    unmount()
  })
})

describe('ReportCard - actual vs planned', () => {
  it('reads "On plan" when block time matched the plan', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ outUtc: '2026-08-11T09:00:00Z', inUtc: '2026-08-11T10:15:00Z', plannedBlockMinutes: 75 }) }),
    })
    expect(text(container)).toContain('On plan')
    unmount()
  })

  it('reads ahead of schedule (success) when the flight took less time than planned', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ outUtc: '2026-08-11T09:00:00Z', inUtc: '2026-08-11T10:00:00Z', plannedBlockMinutes: 75 }) }),
    })
    expect(text(container)).toContain('ahead of schedule')
    unmount()
  })

  it('reads behind schedule when the flight took longer than planned', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ outUtc: '2026-08-11T09:00:00Z', inUtc: '2026-08-11T10:30:00Z', plannedBlockMinutes: 75 }) }),
    })
    expect(text(container)).toContain('behind schedule')
    unmount()
  })

  it('shows fuel used against the plan, under plan reading as the good outcome', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ fuelUsedKg: 1900, fuelPlannedKg: 2200 }) }),
    })
    const body = text(container)
    expect(body).toContain('under plan')
    unmount()
  })
})

describe('ReportCard - fuel', () => {
  it('shows the fuel cost from the posted Fuel ledger line', async () => {
    const { container, unmount } = await render({
      detail: detail({ ledgerTransactions: [ledgerLine({ category: 'Fuel', amount: -3500, description: 'Fuel: 4,000 kg burned, billed at EGGD @ 0.8750/kg' })] }),
    })
    expect(text(container)).toContain('$3,500.00')
    unmount()
  })

  it('shows a zero fuel cost when no Fuel ledger line was posted', async () => {
    const { container, unmount } = await render({
      detail: detail({ ledgerTransactions: [ledgerLine({ category: 'TicketRevenue', amount: 21000, description: 'Ticket revenue' })] }),
    })
    expect(text(container)).toContain('$0.00')
    unmount()
  })
})

describe('ReportCard - VATSIM corroboration', () => {
  it('shows the flown-online card with callsign when corroborated', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ vatsimOnline: true, vatsimCallsign: 'FSO204', vatsimOnlineFraction: 0.92, vatsimControllersWorked: 'EGGD_TWR, EGPH_APP' }) }),
    })
    const body = text(container)
    expect(body).toContain('Flown online')
    expect(body).toContain('FSO204')
    expect(body).toContain('92%')
    expect(body).toContain('EGGD_TWR, EGPH_APP')
    unmount()
  })

  it('shows no VATSIM card when never checked', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ vatsimOnline: null }) }) })
    expect(text(container)).not.toContain('Flown online')
    unmount()
  })
})

describe('ReportCard - flight integrity: sim rate elevation never invalidates payment on its own', () => {
  it('explains that block time and OTP are not measured when the sim rate was elevated, without calling the sector invalid', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ simRateElevated: true, maxSimulationRateObserved: 4, slewDetected: false, positionJumpDetected: false }) }),
    })
    const body = text(container)
    expect(body).toContain('Time acceleration was used during this flight')
    expect(body).toContain('up to 4.0x')
    expect(body).toContain('not measured')
    expect(body).toContain('Landing quality is unaffected')
    expect(body).not.toContain('not valid for payment')
    unmount()
  })

  it('shows no integrity card at all on a clean flight, so a warning is never seen where nothing is wrong', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ simRateElevated: false, slewDetected: false, positionJumpDetected: false }) }) })
    expect(text(container)).not.toContain('Flight integrity')
    unmount()
  })
})

describe('ReportCard - flight integrity: slew and position-jump invalidate payment', () => {
  it('says the sector is not valid for payment when slew was detected', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ slewDetected: true, positionJumpDetected: false }) }),
    })
    const body = text(container)
    expect(body).toContain('Slew was active during this flight.')
    expect(body).toContain('This sector is not valid for payment.')
    unmount()
  })

  it('says the sector is not valid for payment when a position jump was detected', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ slewDetected: false, positionJumpDetected: true }) }),
    })
    const body = text(container)
    expect(body).toContain('Telemetry showed a position change inconsistent with normal flight.')
    expect(body).toContain('This sector is not valid for payment.')
    unmount()
  })

  it('combines both messages when slew and a position jump were both detected', async () => {
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ slewDetected: true, positionJumpDetected: true }) }),
    })
    expect(text(container)).toContain('Slew was active and telemetry showed a position jump during this flight.')
    unmount()
  })

  it('notes in the financial outcome that the sector was not valid for payment, only fuel already bought stayed charged', async () => {
    const { container, unmount } = await render({
      detail: detail({
        flight: flight({ positionJumpDetected: true }),
        ledgerTransactions: [ledgerLine({ category: 'Fuel', amount: -3500, description: 'Fuel: 2,100 kg burned' })],
      }),
    })
    const body = text(container)
    expect(body).toContain("wasn’t valid for payment")
    expect(body).toContain('no ticket revenue was posted')
    unmount()
  })

  it('does not mention payment validity in the financial outcome for a clean flight', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ slewDetected: false, positionJumpDetected: false }) }) })
    expect(text(container)).not.toContain('valid for payment')
    unmount()
  })
})

describe('ReportCard - aircraft type mismatch is noted, never penalised', () => {
  it('shows the mismatch card with the flown and expected types, saying explicitly it does not affect payment', async () => {
    const payload: MismatchPayload = {
      titleFlown: 'Boeing 737-800',
      atcModel: 'B738',
      expectedFamily: 'A320',
      expectedType: 'A20N',
    }
    const { container, unmount } = await render({
      detail: detail({ flight: flight({ typeMismatch: true }), events: [touchdownEvent('td-1'), mismatchEvent(payload)] }),
    })
    const body = text(container)
    expect(body).toContain('Aircraft type noted, not penalised')
    expect(body).toContain('Boeing 737-800')
    expect(body).toContain('A320')
    expect(body).toContain('A20N')
    expect(body).toContain('it does not affect payment')
    unmount()
  })

  it('shows no mismatch card at all when the type matched', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ typeMismatch: false }) }) })
    expect(text(container)).not.toContain('Aircraft type noted')
    unmount()
  })

  it('shows no mismatch card when the sim never reported an aircraft to check (null, not a mismatch)', async () => {
    const { container, unmount } = await render({ detail: detail({ flight: flight({ typeMismatch: null }) }) })
    expect(text(container)).not.toContain('Aircraft type noted')
    unmount()
  })

  it('leaves ticket revenue in the ledger untouched by a type mismatch - the mismatch never subtracts a penalty', async () => {
    const payload: MismatchPayload = { titleFlown: 'Boeing 737-800', atcModel: 'B738', expectedFamily: 'A320', expectedType: 'A20N' }
    const { container, unmount } = await render({
      detail: detail({
        flight: flight({ typeMismatch: true, revenue: 21000, totalCost: 13500 }),
        events: [touchdownEvent('td-1'), mismatchEvent(payload)],
        ledgerTransactions: [
          ledgerLine({ id: 'l1', category: 'TicketRevenue', amount: 21000, description: 'Ticket revenue (148 passengers)' }),
        ],
      }),
    })
    const body = text(container)
    // Full ticket revenue posted, no separate "mismatch"/"penalty" deduction line anywhere.
    expect(body).toContain('$21,000.00')
    expect(body.toLowerCase()).not.toContain('penalty')
    unmount()
  })
})

describe('ReportCard - financial outcome', () => {
  it('shows net as revenue minus cost with a positive sign implied (no minus) for a profit', async () => {
    const { container, unmount } = await render({
      detail: detail({
        ledgerTransactions: [
          ledgerLine({ id: 'l1', category: 'TicketRevenue', amount: 21000, description: 'Ticket revenue' }),
          ledgerLine({ id: 'l2', category: 'Fuel', amount: -3500, description: 'Fuel: 2,100 kg burned' }),
        ],
      }),
    })
    const body = text(container)
    expect(body).toContain('$17,500.00')
    unmount()
  })

  it('shows a loss as negative with exactly one minus sign', async () => {
    const { container, unmount } = await render({
      detail: detail({
        ledgerTransactions: [
          ledgerLine({ id: 'l1', category: 'TicketRevenue', amount: 3000, description: 'Ticket revenue' }),
          ledgerLine({ id: 'l2', category: 'Fuel', amount: -3500, description: 'Fuel: 2,100 kg burned' }),
        ],
      }),
    })
    const body = text(container)
    expect(body).toContain('-$500.00')
    expect(body).not.toContain('--')
    unmount()
  })

  it('says no financial lines were posted rather than showing an empty list', async () => {
    const { container, unmount } = await render({ detail: detail({ ledgerTransactions: [] }) })
    expect(text(container)).toContain('No financial lines were posted for this flight.')
    unmount()
  })
})

describe('ReportCard - header', () => {
  it('shows the route and callsign built from the airline code', async () => {
    const { container, unmount } = await render()
    const body = text(container)
    expect(body).toContain('EGGD → EGPH')
    expect(body).toContain('FSO204')
    unmount()
  })

  it('falls back to a generic title when the route could not be resolved', async () => {
    const { container, unmount } = await render({ route: null })
    expect(text(container)).toContain('Flight report')
    unmount()
  })
})
