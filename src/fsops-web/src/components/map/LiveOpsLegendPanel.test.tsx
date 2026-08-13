import { beforeEach, describe, expect, it } from 'vitest'

import { LiveOpsLegendPanel } from './LiveOpsLegendPanel'
import { click, flush, getByRole, mount, queryByRole, text } from '@/test/domHarness'
import type { VatsimAtcController, VatsimTrafficPilot } from '@/types/operations'

const STORAGE_KEY = 'fsops-liveops-legend-collapsed'

beforeEach(() => {
  window.localStorage.clear()
})

function controller(overrides: Partial<VatsimAtcController> = {}): VatsimAtcController {
  return {
    callsign: 'EGLL_TWR',
    facilityLabel: 'Tower',
    frequency: '118.500',
    coverageKind: 'terminal',
    airportIcao: 'EGLL',
    airportName: 'London Heathrow',
    latitudeDeg: 51.47,
    longitudeDeg: -0.4543,
    visualRangeNm: 30,
    boundaryId: null,
    boundaryName: null,
    inNetwork: true,
    logonTimeUtc: '2026-08-13T10:00:00Z',
    ...overrides,
  }
}

function traffic(overrides: Partial<VatsimTrafficPilot> = {}): VatsimTrafficPilot {
  return {
    callsign: 'BAW123',
    latitudeDeg: 51.5,
    longitudeDeg: -0.1,
    altitudeFt: 35000,
    groundSpeedKt: 420,
    headingDeg: 90,
    departureIcao: 'EGLL',
    arrivalIcao: 'EGCC',
    ...overrides,
  }
}

async function render(props: { hasAircraft?: boolean; atcControllers?: VatsimAtcController[]; vatsimTraffic?: VatsimTrafficPilot[] } = {}) {
  return mount(
    <LiveOpsLegendPanel
      hasAircraft={props.hasAircraft ?? false}
      atcControllers={props.atcControllers ?? []}
      vatsimTraffic={props.vatsimTraffic ?? []}
    />,
  )
}

describe('LiveOpsLegendPanel - nothing to key', () => {
  it('renders nothing when there is no aircraft, traffic or ATC to explain', async () => {
    const { container, unmount } = await render()
    expect(container.querySelector('div')).toBeNull()
    unmount()
  })
})

describe('LiveOpsLegendPanel - showing the key', () => {
  it('shows the legend with a hide control when there is something to key', async () => {
    const { container, unmount } = await render({ hasAircraft: true })
    expect(text(container)).toContain('You')
    expect(text(container)).toContain('Virtual')
    expect(getByRole(container, 'button', { name: 'Hide the live map legend' })).toBeTruthy()
    unmount()
  })

  it('includes the ATC and other-traffic rows only when there is content for them', async () => {
    const { container, unmount } = await render({
      atcControllers: [controller()],
      vatsimTraffic: [traffic()],
    })
    expect(text(container)).toContain('ATC')
    expect(text(container)).toContain('Other VATSIM traffic')
    unmount()
  })
})

describe('LiveOpsLegendPanel - collapsing to a reopenable pill', () => {
  it('collapses to a small pill on hide, with an obvious way back', async () => {
    const { container, unmount } = await render({ hasAircraft: true })

    click(getByRole(container, 'button', { name: 'Hide the live map legend' }))
    await flush()

    expect(queryByRole(container, 'button', { name: 'Hide the live map legend' })).toBeNull()
    const pill = getByRole(container, 'button', { name: 'Show the live map legend' })
    expect(pill).toBeTruthy()

    click(pill)
    await flush()
    expect(getByRole(container, 'button', { name: 'Hide the live map legend' })).toBeTruthy()
    unmount()
  })

  it('persists the collapsed choice in localStorage, consistent with the empty state', async () => {
    const { container, unmount } = await render({ hasAircraft: true })
    click(getByRole(container, 'button', { name: 'Hide the live map legend' }))
    await flush()
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')
    unmount()

    const second = await render({ hasAircraft: true })
    expect(queryByRole(second.container, 'button', { name: 'Hide the live map legend' })).toBeNull()
    expect(getByRole(second.container, 'button', { name: 'Show the live map legend' })).toBeTruthy()
    second.unmount()
  })
})
