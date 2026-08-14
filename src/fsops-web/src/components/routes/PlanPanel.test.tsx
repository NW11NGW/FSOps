import type { ComponentProps } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { PlanPanel } from './PlanPanel'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, findButton, flush, isDisabled, mount, text, typeInto } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { AirportSummary } from '@/types/airport'
import type { RoutePreviewResponse } from '@/types/route'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

type Props = ComponentProps<typeof PlanPanel>

function airport(overrides: Partial<AirportSummary> = {}): AirportSummary {
  return {
    icao: 'EGGD',
    iata: 'BRS',
    name: 'Bristol Airport',
    municipality: 'Bristol',
    country: 'GB',
    latitude: 51.3827,
    longitude: -2.7191,
    elevationFt: 622,
    sizeCategory: 'Medium',
    hasScheduledService: true,
    longestRunwayFt: 8000,
    ...overrides,
  }
}

const departure = airport()
const arrival = airport({ icao: 'EGPH', iata: 'EDI', name: 'Edinburgh Airport', municipality: 'Edinburgh', longestRunwayFt: 8500 })

function preview(overrides: Partial<RoutePreviewResponse> = {}): RoutePreviewResponse {
  return {
    distanceNm: 280,
    initialBearingDeg: 15,
    estimatedBlockMinutes: 65,
    blockTimeBreakdown: null,
    cruiseAltitudeFt: 33000,
    blockFuelKg: 2500,
    fuelBreakdown: null,
    fuelPricePerKg: null,
    suggestedFare: 90,
    economics: null,
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

function baseProps(overrides: Partial<Props> = {}): Props {
  return {
    departure,
    arrival,
    preview: preview(),
    status: 'success',
    isRefreshing: false,
    errorMessage: null,
    dangerMessage: null,
    advisoryWarnings: [],
    fareOverrideValue: '90.00',
    onFareOverrideChange: vi.fn(),
    onCreate: vi.fn(),
    creating: false,
    createError: null,
    canCreate: true,
    ...overrides,
  }
}

async function render(overrides: Partial<Props> = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <PlanPanel {...baseProps(overrides)} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('PlanPanel - before both airports are picked', () => {
  it('prompts to pick both airports when neither is selected', async () => {
    const { container, unmount } = await render({ departure: null, arrival: null })

    expect(text(container)).toContain('Pick both airports to see a live plan')
    expect(text(container)).not.toContain('Live plan')

    unmount()
  })

  it('still prompts when only the departure is picked', async () => {
    const { container, unmount } = await render({ arrival: null })

    expect(text(container)).toContain('Pick both airports to see a live plan')

    unmount()
  })
})

describe('PlanPanel - loading and error before any figures arrive', () => {
  it('shows no stats yet while the first preview is loading', async () => {
    const { container, unmount } = await render({ status: 'loading', preview: null })

    expect(text(container)).not.toContain('Distance')
    expect(text(container)).not.toContain('Suggested fare')

    unmount()
  })

  it('shows the error message when the first load fails, not stale or zeroed figures', async () => {
    const { container, unmount } = await render({ status: 'error', preview: null, errorMessage: 'Could not load a preview for this pair.' })

    expect(text(container)).toContain('Could not load a preview for this pair.')
    expect(text(container)).not.toContain('Distance')

    unmount()
  })
})

describe('PlanPanel - the live plan figures', () => {
  it('shows distance, block time, altitude, fuel and the suggested fare once a preview arrives', async () => {
    const { container, unmount } = await render()

    const body = text(container)
    expect(body).toContain('280 nm')
    expect(body).toContain('1h 5m')
    expect(body).toContain('33,000 ft')
    expect(body).toContain('2,500 kg')
    expect(body).toContain('$90.00')
    expect(body).toContain('EGGD → EGPH')

    unmount()
  })
})

describe('PlanPanel - the four fleet-feasibility states (range and runway)', () => {
  it('says nothing extra when a reserved aircraft can already fly it (ReservedCanFly / ReservedCanUse)', async () => {
    const { container, unmount } = await render({ dangerMessage: null, advisoryWarnings: [], canCreate: true })

    expect(text(container)).not.toContain('can’t be created yet')
    const createButton = findButton(container, 'Create route')
    expect(isDisabled(createButton)).toBe(false)

    unmount()
  })

  it('shows a non-blocking advisory - and never disables Create - when only an unreserved fleet aircraft can fly it (RequiresReservation)', async () => {
    const advisory = 'Reserve G-EFGH (A321neo) to fly this route - nothing reserved to you can reach it yet.'
    const { container, unmount } = await render({
      dangerMessage: null,
      advisoryWarnings: [advisory],
      canCreate: true,
    })

    expect(text(container)).toContain(advisory)
    expect(text(container)).not.toContain('can’t be created yet')
    const createButton = findButton(container, 'Create route')
    expect(isDisabled(createButton)).toBe(false)

    unmount()
  })

  it('blocks creation with a danger banner when nothing in the fleet can fly it (BeyondFleet)', async () => {
    const blockingMessage = 'This route is beyond the range of every aircraft in your fleet.'
    const { container, unmount } = await render({
      dangerMessage: blockingMessage,
      advisoryWarnings: [],
      canCreate: false,
    })

    expect(text(container)).toContain('can’t be created yet')
    expect(text(container)).toContain(blockingMessage)
    const createButton = findButton(container, 'Create route')
    expect(isDisabled(createButton)).toBe(true)

    unmount()
  })

  it('shows an informational note but still allows creation when there is no fleet yet (NotAssessed)', async () => {
    const note = 'No aircraft in your fleet yet - range and runway suitability have not been checked.'
    const { container, unmount } = await render({
      dangerMessage: null,
      advisoryWarnings: [note],
      canCreate: true,
    })

    expect(text(container)).toContain(note)
    expect(text(container)).not.toContain('can’t be created yet')
    const createButton = findButton(container, 'Create route')
    expect(isDisabled(createButton)).toBe(false)

    unmount()
  })
})

describe('PlanPanel - fare override and create', () => {
  it('reports fare input changes through onFareOverrideChange rather than holding its own state', async () => {
    const onFareOverrideChange = vi.fn()
    const { container, unmount } = await render({ onFareOverrideChange })

    const input = container.querySelector<HTMLInputElement>('#fare-override')
    if (!input) throw new Error('fare-override input not found')
    typeInto(input, '125.50')

    expect(onFareOverrideChange).toHaveBeenCalledWith('125.50')

    unmount()
  })

  it('calls onCreate when Create route is clicked and creation is allowed', async () => {
    const onCreate = vi.fn()
    const { container, unmount } = await render({ onCreate, canCreate: true })

    click(findButton(container, 'Create route'))

    expect(onCreate).toHaveBeenCalledOnce()

    unmount()
  })

  it('shows "Creating…" while a create request is in flight', async () => {
    const { container, unmount } = await render({ creating: true })

    expect(text(container)).toContain('Creating…')
    expect(text(container)).not.toContain('Create route')

    unmount()
  })

  it('shows the create error message when creation fails', async () => {
    const { container, unmount } = await render({ createError: 'Could not create this route. Try again.' })

    expect(text(container)).toContain('Could not create this route. Try again.')

    unmount()
  })
})
