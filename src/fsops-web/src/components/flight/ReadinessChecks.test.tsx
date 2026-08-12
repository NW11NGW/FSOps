import { describe, expect, it } from 'vitest'

import { ReadinessChecks } from './ReadinessChecks'
import { mount, text } from '@/test/domHarness'
import type { AircraftOptionRow } from '@/components/flight/routeRow'
import type { SimStatus, TelemetryPayload } from '@/types/flight'

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

function simStatus(overrides: Partial<SimStatus> = {}): SimStatus {
  return {
    state: 'Connected',
    sourceKind: 'Fake',
    aircraftTitle: 'Airbus A320neo Asobo',
    lastSampleUtc: '2026-08-12T09:00:00Z',
    ...overrides,
  }
}

function telemetry(overrides: Partial<TelemetryPayload> = {}): TelemetryPayload {
  return {
    timestampUtc: '2026-08-12T09:00:00Z',
    latitude: 51.1481,
    longitude: -0.1903,
    altitudeMslFt: 202,
    altitudeAglFt: 0,
    indicatedAirspeedKt: 0,
    groundSpeedKt: 0,
    verticalSpeedFpm: 0,
    headingTrue: 260,
    headingMagnetic: 258,
    onGround: true,
    connectionState: 'Connected',
    ...overrides,
  }
}

const departure = { icao: 'EGLL', latitude: 51.1481, longitude: -0.1903 }
// Exactly 1 degree of latitude further north, longitude unchanged - the haversine formula's
// longitude term drops out entirely at dLon=0, so this is ~60.04 nm regardless of the exact
// starting latitude, giving a round, predictable "far away" figure.
const farAway = { latitude: departure.latitude + 1, longitude: departure.longitude }

async function render(overrides: {
  simStatus?: SimStatus | null
  simStatusLoaded?: boolean
  telemetry?: TelemetryPayload | null
  selectedAircraft?: AircraftOptionRow | null
  departure?: { icao: string; latitude: number; longitude: number } | null
}) {
  const mounted = await mount(
    <ReadinessChecks
      simStatus={overrides.simStatus === undefined ? simStatus() : overrides.simStatus}
      simStatusLoaded={overrides.simStatusLoaded ?? true}
      telemetry={overrides.telemetry === undefined ? telemetry() : overrides.telemetry}
      selectedAircraft={overrides.selectedAircraft === undefined ? aircraft() : overrides.selectedAircraft}
      departure={overrides.departure === undefined ? departure : overrides.departure}
    />,
  )
  return mounted
}

describe('ReadinessChecks - simulator connection', () => {
  it('reads "Checking…" before the first sim-status response has arrived', async () => {
    const { container, unmount } = await render({ simStatusLoaded: false })
    expect(text(container)).toContain('Checking…')
    unmount()
  })

  it('names the connected source once the sim is connected', async () => {
    const { container, unmount } = await render({ simStatus: simStatus({ state: 'Connected', sourceKind: 'Fake' }) })
    expect(text(container)).toContain('Connected (Fake)')
    unmount()
  })

  it('warns that telemetry will not be tracked when the sim is not connected', async () => {
    const { container, unmount } = await render({ simStatus: simStatus({ state: 'Disconnected' }) })
    expect(text(container)).toContain("telemetry won't be tracked until MSFS connects")
    unmount()
  })
})

describe('ReadinessChecks - aircraft loaded in sim', () => {
  it('asks to pick an aircraft when none is selected yet', async () => {
    const { container, unmount } = await render({ selectedAircraft: null })
    expect(text(container)).toContain('Pick an aircraft to compare')
    unmount()
  })

  it('says the sim is not reporting an aircraft yet when the sim has none loaded', async () => {
    const { container, unmount } = await render({ simStatus: simStatus({ aircraftTitle: null }) })
    expect(text(container)).toContain("Sim isn't reporting a loaded aircraft yet")
    unmount()
  })

  it('says the sim title looks right when it contains the expected ICAO type as a substring', async () => {
    // The check is a raw case-insensitive substring match on the aircraft title, not a real type
    // parse - "Airbus A320neo Asobo" literally contains "A320", which is what makes this pass.
    const { container, unmount } = await render({
      simStatus: simStatus({ aircraftTitle: 'Airbus A320neo Asobo' }),
      selectedAircraft: aircraft({ icaoType: 'A320' }),
    })
    const body = text(container)
    expect(body).toContain('looks right for A320')
    unmount()
  })

  it('flags a mismatch without treating it as an error, by naming both what is loaded and what is expected', async () => {
    const { container, unmount } = await render({
      simStatus: simStatus({ aircraftTitle: 'Boeing 737-800' }),
      selectedAircraft: aircraft({ icaoType: 'A20N' }),
    })
    const body = text(container)
    expect(body).toContain('Sim has "Boeing 737-800" loaded — route expects A20N')
    unmount()
  })

  it('falls back to family when the aircraft has no ICAO type available to compare', async () => {
    const { container, unmount } = await render({
      simStatus: simStatus({ aircraftTitle: 'Boeing 737-800' }),
      selectedAircraft: aircraft({ icaoType: '', family: 'A320' }),
    })
    expect(text(container)).toContain('route expects A320')
    unmount()
  })
})

describe('ReadinessChecks - parked at departure', () => {
  it('asks to pick a route when there is no departure to check against', async () => {
    const { container, unmount } = await render({ departure: null })
    expect(text(container)).toContain('Pick a route to check')
    unmount()
  })

  it('says there is no position data yet when telemetry has not arrived', async () => {
    const { container, unmount } = await render({ telemetry: null })
    expect(text(container)).toContain('No position data from the sim yet')
    unmount()
  })

  it('confirms parked at the departure airport when on the ground and close by', async () => {
    const { container, unmount } = await render({ telemetry: telemetry({ onGround: true, ...departure }) })
    expect(text(container)).toContain('On the ground at EGLL')
    unmount()
  })

  it('warns with the distance when on the ground but far from the departure airport', async () => {
    const { container, unmount } = await render({ telemetry: telemetry({ onGround: true, ...farAway }) })
    expect(text(container)).toContain('On the ground, 60 nm from EGLL')
    unmount()
  })

  it('warns with the distance when airborne, distinctly from the on-the-ground case', async () => {
    const { container, unmount } = await render({ telemetry: telemetry({ onGround: false, ...farAway }) })
    const body = text(container)
    expect(body).toContain('Aircraft is airborne, 60 nm from EGLL')
    expect(body).not.toContain('On the ground')
    unmount()
  })
})
