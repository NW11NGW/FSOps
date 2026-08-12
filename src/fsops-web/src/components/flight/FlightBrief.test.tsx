import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'

import { FlightBrief } from './FlightBrief'
import type { AircraftOptionRow, RouteRow } from './routeRow'
import { SettingsProvider } from '@/hooks/useSettings'
import type { FlightPreviewStatus } from '@/hooks/useFlightPreview'
import { click, flush, getByRole, isDisabled, mount, queryAllByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { RoutePreviewResponse } from '@/types/route'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function aircraft(overrides: Partial<AircraftOptionRow> = {}): AircraftOptionRow {
  return {
    fleetAircraftId: 'ac-1',
    registration: 'G-ABCD',
    aircraftTypeId: 'type-1',
    aircraftTypeName: 'Airbus A320neo',
    paxCapacity: 180,
    estimatedBlockMinutes: 75,
    isFlyable: true,
    reason: null,
    icaoType: 'A20N',
    family: 'A320',
    ...overrides,
  }
}

function row(overrides: Partial<RouteRow> = {}): RouteRow {
  return {
    routeId: 'route-1',
    flightNumber: '204',
    departureIcao: 'EGGD',
    departureName: 'Bristol',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh',
    distanceNm: 300,
    blockMinutes: 75,
    baseFare: 90,
    isFlyable: true,
    reason: null,
    aircraftOptions: [aircraft()],
    aircraftUnknown: false,
    ...overrides,
  }
}

function preview(overrides: Partial<RoutePreviewResponse> = {}): RoutePreviewResponse {
  return {
    distanceNm: 300,
    initialBearingDeg: 10,
    estimatedBlockMinutes: 75,
    blockTimeBreakdown: {
      taxiOutMinutes: 12,
      climbMinutes: 18,
      cruiseMinutes: 30,
      descentMinutes: 10,
      taxiInMinutes: 5,
      totalMinutes: 75,
    },
    cruiseAltitudeFt: 32000,
    blockFuelKg: 2200,
    fuelBreakdown: {
      tripFuelKg: 1800,
      taxiFuelKg: 100,
      contingencyFuelKg: 200,
      alternateFuelKg: 80,
      finalReserveFuelKg: 20,
      totalFuelKg: 2200,
      costOfCarryKg: 0,
    },
    fuelPricePerKg: 0.8,
    destinationFuelPricePerKg: 0.85,
    tankeringAdvisory: null,
    suggestedFare: 95,
    economics: {
      fare: 95,
      referenceFare: 95,
      expectedPassengers: 150,
      loadFactorPercent: 83,
      expectedRevenuePerSector: 14250,
    },
    greatCirclePath: [],
    validation: {
      withinRange: true,
      sameAirport: false,
      warnings: [],
      range: { verdict: 'ReservedCanFly', blocking: false, message: null, aircraftRegistration: null, aircraftTypeName: null, operationalRangeNm: null },
      runway: { verdict: 'ReservedCanUse', blocking: false, message: null, aircraftRegistration: null, aircraftTypeName: null },
    },
    ...overrides,
  }
}

async function render(props: Partial<Parameters<typeof FlightBrief>[0]> = {}) {
  const mounted = await mount(
    <MemoryRouter>
    <SettingsProvider>
      <FlightBrief
        row={props.row ?? row()}
        aircraftOptions={props.aircraftOptions ?? [aircraft()]}
        selectedAircraft={props.selectedAircraft === undefined ? aircraft() : props.selectedAircraft}
        onSelectAircraft={props.onSelectAircraft ?? vi.fn()}
        preview={props.preview === undefined ? preview() : props.preview}
        previewStatus={props.previewStatus ?? ('success' as FlightPreviewStatus)}
        airlineIcaoCode={props.airlineIcaoCode === undefined ? 'FSO' : props.airlineIcaoCode}
        simStatus={props.simStatus === undefined ? null : props.simStatus}
        simStatusLoaded={props.simStatusLoaded ?? true}
        telemetry={props.telemetry === undefined ? null : props.telemetry}
        departureCoords={props.departureCoords === undefined ? null : props.departureCoords}
        onStart={props.onStart ?? vi.fn()}
        starting={props.starting ?? false}
        startError={props.startError === undefined ? null : props.startError}
      />
    </SettingsProvider>
    </MemoryRouter>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('FlightBrief - aircraft selection', () => {
  it('calls onSelectAircraft when a flyable aircraft is clicked', async () => {
    const onSelectAircraft = vi.fn()
    const options = [aircraft({ fleetAircraftId: 'ac-1', registration: 'G-AAAA' }), aircraft({ fleetAircraftId: 'ac-2', registration: 'G-BBBB' })]
    const { container, unmount } = await render({ aircraftOptions: options, onSelectAircraft })

    const target = queryAllByRole(container, 'button').find((b) => b.textContent?.includes('G-BBBB'))
    expect(target).toBeDefined()
    click(target!)

    expect(onSelectAircraft).toHaveBeenCalledWith(options[1])
    unmount()
  })

  it('never calls onSelectAircraft for an unflyable aircraft, and explains why in the page text', async () => {
    const onSelectAircraft = vi.fn()
    const options = [aircraft({ fleetAircraftId: 'ac-1', registration: 'G-GRND', isFlyable: false, reason: 'Reserved for another route.' })]
    const { container, unmount } = await render({ aircraftOptions: options, onSelectAircraft })

    const target = queryAllByRole(container, 'button').find((b) => b.textContent?.includes('G-GRND'))
    expect(target).toBeDefined()
    click(target!)

    expect(onSelectAircraft).not.toHaveBeenCalled()
    expect(text(container)).toContain('Reserved for another route.')
    unmount()
  })

  it('links to the Fleet page only when the unflyable reason actually mentions it', async () => {
    const mentionsFleetPage = [aircraft({ fleetAircraftId: 'ac-1', isFlyable: false, reason: 'Reserved - release it on the Fleet page.' })]
    const withLink = await render({ aircraftOptions: mentionsFleetPage })
    expect(getByRole(withLink.container, 'link', { name: 'Open the Fleet page' })).toBeTruthy()
    withLink.unmount()

    const doesNotMention = [aircraft({ fleetAircraftId: 'ac-1', isFlyable: false, reason: 'In maintenance.' })]
    const withoutLink = await render({ aircraftOptions: doesNotMention })
    expect(queryAllByRole(withoutLink.container, 'link', { name: 'Open the Fleet page' })).toHaveLength(0)
    withoutLink.unmount()
  })

  it('explains when no aircraft is at the departure airport at all', async () => {
    const { container, unmount } = await render({ aircraftOptions: [], row: row({ aircraftUnknown: false, reason: 'No aircraft is at this departure airport.' }) })
    expect(text(container)).toContain('No aircraft is at this departure airport.')
    unmount()
  })

  it('says aircraft availability is not known yet when the row came from the fallback path', async () => {
    const { container, unmount } = await render({ aircraftOptions: [], row: row({ aircraftUnknown: true }) })
    expect(text(container)).toContain("Aircraft assignment isn't known for this route yet")
    unmount()
  })
})

describe('FlightBrief - preview loading state', () => {
  it('shows no stat labels while the first preview is still loading', async () => {
    const { container, unmount } = await render({ preview: null, previewStatus: 'loading' })
    const body = text(container)
    expect(body).not.toContain('Distance')
    expect(body).not.toContain('Cruise altitude')
    unmount()
  })

  it('shows the real preview figures once loaded', async () => {
    const { container, unmount } = await render({ preview: preview({ distanceNm: 300, cruiseAltitudeFt: 32000 }) })
    const body = text(container)
    expect(body).toContain('Distance')
    expect(body).toContain('300 nm')
    expect(body).toContain('32,000 ft')
    unmount()
  })

  it('shows expected passengers against capacity and expected revenue from the real economics figures', async () => {
    const { container, unmount } = await render({
      selectedAircraft: aircraft({ paxCapacity: 180 }),
      preview: preview({ economics: { fare: 95, referenceFare: 95, expectedPassengers: 150, loadFactorPercent: 83, expectedRevenuePerSector: 14250 } }),
    })
    const body = text(container)
    expect(body).toContain('150 / 180')
    expect(body).toContain('$14,250.00')
    expect(body).toContain('83% load factor')
    unmount()
  })
})

describe('FlightBrief - block time and fuel breakdown tabs', () => {
  it('shows the block time breakdown by default and switches to fuel on tab click', async () => {
    const { container, unmount } = await render()

    let body = text(container)
    expect(body).toContain('Taxi out')
    expect(body).toContain('Climb')
    expect(body).not.toContain('Trip fuel')

    const fuelTab = getByRole(container, 'tab', { name: 'Fuel' })
    click(fuelTab)

    body = text(container)
    expect(body).toContain('Trip fuel')
    expect(body).toContain('Taxi fuel')
    // Inactive tab panels unmount rather than merely hiding - the block-time-only label from the
    // previous tab is gone, not just visually covered. ("Cruise" isn't checked here - the
    // "Cruise altitude" stat tile above contains it regardless of which breakdown tab is active.)
    expect(body).not.toContain('Taxi out')

    unmount()
  })

  it('shows fuel prices at both ends when known', async () => {
    const { container, unmount } = await render({ preview: preview({ fuelPricePerKg: 0.8, destinationFuelPricePerKg: 0.85 }) })

    click(getByRole(container, 'tab', { name: 'Fuel' }))

    const body = text(container)
    expect(body).toContain('Price here')
    expect(body).toContain('Price at destination')
    unmount()
  })
})

describe('FlightBrief - destination fuel price hint', () => {
  it('is silent when the destination price is not materially higher', async () => {
    const { container, unmount } = await render({ preview: preview({ fuelPricePerKg: 0.8, destinationFuelPricePerKg: 0.82 }) })
    expect(text(container)).not.toContain('more expensive at')
    unmount()
  })

  it('flags the price gap and recommends tankering when it would pay off', async () => {
    const { container, unmount } = await render({
      preview: preview({
        fuelPricePerKg: 0.8,
        destinationFuelPricePerKg: 1.2,
        tankeringAdvisory: {
          departurePricePerKg: 0.8,
          destinationPricePerKg: 1.2,
          extraFuelToCarryKg: 500,
          costOfCarryKg: 20,
          costOfCarryAmount: 16,
          grossSavingAmount: 200,
          netSavingAmount: 184,
          recommended: true,
          exceedsMtow: false,
        },
      }),
    })
    const body = text(container)
    expect(body).toContain('more expensive at EGPH than here')
    expect(body).toContain('could save about $184.00')
    unmount()
  })

  it('says the extra weight outweighs the saving when tankering is not recommended', async () => {
    const { container, unmount } = await render({
      preview: preview({
        fuelPricePerKg: 0.8,
        destinationFuelPricePerKg: 1.2,
        tankeringAdvisory: {
          departurePricePerKg: 0.8,
          destinationPricePerKg: 1.2,
          extraFuelToCarryKg: 500,
          costOfCarryKg: 400,
          costOfCarryAmount: 320,
          grossSavingAmount: 200,
          netSavingAmount: -120,
          recommended: false,
          exceedsMtow: false,
        },
      }),
    })
    expect(text(container)).toContain('outweighs the saving')
    unmount()
  })
})

describe('FlightBrief - starting the flight', () => {
  it('disables Start flight when the route cannot be flown and aircraft assignment is known', async () => {
    const { container, unmount } = await render({ row: row({ isFlyable: false, aircraftUnknown: false }) })
    expect(isDisabled(getByRole(container, 'button', { name: /Start flight/ }))).toBe(true)
    unmount()
  })

  it('allows starting when aircraft assignment is unknown even if isFlyable is false', async () => {
    const { container, unmount } = await render({ row: row({ isFlyable: false, aircraftUnknown: true }) })
    expect(isDisabled(getByRole(container, 'button', { name: /Start flight/ }))).toBe(false)
    unmount()
  })

  it('shows "Starting…" and disables the button while a start is in flight', async () => {
    const { container, unmount } = await render({ starting: true })
    const button = getByRole(container, 'button', { name: 'Starting…' })
    expect(isDisabled(button)).toBe(true)
    unmount()
  })

  it('calls onStart when Start flight is clicked', async () => {
    const onStart = vi.fn()
    const { container, unmount } = await render({ onStart })

    click(getByRole(container, 'button', { name: /Start flight/ }))

    expect(onStart).toHaveBeenCalledTimes(1)
    unmount()
  })

  it('shows the start error message when starting failed', async () => {
    const { container, unmount } = await render({ startError: 'The aircraft has since been reserved by another route.' })
    expect(text(container)).toContain('The aircraft has since been reserved by another route.')
    unmount()
  })

  it('shows the aircraft-unknown badge when assignment could not be determined', async () => {
    const { container, unmount } = await render({ row: row({ aircraftUnknown: true }) })
    expect(text(container)).toContain('Aircraft availability unknown')
    unmount()
  })
})
