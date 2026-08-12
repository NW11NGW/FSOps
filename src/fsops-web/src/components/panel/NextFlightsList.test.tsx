import { describe, expect, it } from 'vitest'

import { NextFlightsList } from './NextFlightsList'
import { mount, queryAllByRole, queryByRole, text } from '@/test/domHarness'
import type { FlightOption } from '@/types/flight'

function option(overrides: Partial<FlightOption> = {}): FlightOption {
  return {
    routeId: 'route-1',
    flightNumber: '204',
    departureIcao: 'EGGD',
    departureName: 'Bristol',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh',
    distanceNm: 300,
    estimatedBlockMinutes: 75,
    isFlyable: true,
    reason: null,
    aircraftOptions: [],
    ...overrides,
  }
}

describe('NextFlightsList', () => {
  it('says nothing is ready rather than showing an empty strip', async () => {
    const { container, unmount } = await mount(<NextFlightsList flights={[]} airlineIcaoCode="FSO" />)

    expect(text(container)).toContain('No route is ready to fly right now.')

    unmount()
  })

  it('shows each route with its callsign', async () => {
    const { container, unmount } = await mount(
      <NextFlightsList
        flights={[option(), option({ routeId: 'route-2', departureIcao: 'EGPH', arrivalIcao: 'EGGD', flightNumber: '205' })]}
        airlineIcaoCode="FSO"
      />,
    )

    const body = text(container)
    expect(body).toContain('FSO204')
    expect(body).toContain('FSO205')
    expect(queryAllByRole(container, 'listitem')).toHaveLength(2)

    unmount()
  })

  it('falls back to the bare flight number when the airline code has not loaded', async () => {
    const { container, unmount } = await mount(<NextFlightsList flights={[option()]} airlineIcaoCode={null} />)

    expect(text(container)).toContain('204')
    expect(text(container)).not.toContain('undefined')
    expect(text(container)).not.toContain('null204')

    unmount()
  })

  it('stays a read-out: nothing in it is a control', async () => {
    const { container, unmount } = await mount(<NextFlightsList flights={[option()]} airlineIcaoCode="FSO" />)

    // Deliberately not a second way to start a flight - a tappable-looking row here would be a
    // trap on a panel whose whole job is a glance.
    expect(queryByRole(container, 'button')).toBeNull()
    expect(queryByRole(container, 'link')).toBeNull()

    unmount()
  })
})
