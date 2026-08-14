import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AirportPickerCard } from './AirportPickerCard'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, getByRole, isDisabled, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { AirportSummary } from '@/types/airport'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

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

async function render(props: {
  departure?: AirportSummary | null
  arrival?: AirportSummary | null
  onSelectDeparture?: (a: AirportSummary) => void
  onSelectArrival?: (a: AirportSummary) => void
  onClearDeparture?: () => void
  onClearArrival?: () => void
  onSwap?: () => void
} = {}) {
  const mounted = await mount(
    <SettingsProvider>
      <AirportPickerCard
        departure={props.departure ?? null}
        arrival={props.arrival ?? null}
        onSelectDeparture={props.onSelectDeparture ?? vi.fn()}
        onSelectArrival={props.onSelectArrival ?? vi.fn()}
        onClearDeparture={props.onClearDeparture ?? vi.fn()}
        onClearArrival={props.onClearArrival ?? vi.fn()}
        onSwap={props.onSwap ?? vi.fn()}
      />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('AirportPickerCard - before anything is picked', () => {
  it('shows a search box for both departure and arrival', async () => {
    const { container, unmount } = await render()

    expect(container.querySelector('input[placeholder="Search departure airport…"]')).not.toBeNull()
    expect(container.querySelector('input[placeholder="Search arrival airport…"]')).not.toBeNull()

    unmount()
  })

  it('disables swap when neither airport is picked - there is nothing to swap', async () => {
    const { container, unmount } = await render()

    const swapButton = getByRole(container, 'button', { name: 'Swap departure and arrival' })
    expect(isDisabled(swapButton)).toBe(true)

    unmount()
  })
})

describe('AirportPickerCard - a selected airport', () => {
  it('shows the picked departure airport’s identity and location once selected', async () => {
    const { container, unmount } = await render({ departure: airport() })

    const body = text(container)
    expect(body).toContain('EGGD')
    expect(body).toContain('BRS')
    expect(body).toContain('Bristol Airport')
    expect(body).toContain('Bristol, GB')
    // No search box remains for the side that's already picked.
    expect(container.querySelector('input[placeholder="Search departure airport…"]')).toBeNull()

    unmount()
  })

  it('shows the runway length when known', async () => {
    const { container, unmount } = await render({ departure: airport({ longestRunwayFt: 8000 }) })

    expect(text(container)).toContain('longest runway')
    expect(text(container)).toContain('8,000 ft')

    unmount()
  })

  it('says runway data is missing rather than showing a blank or a zero', async () => {
    const { container, unmount } = await render({ departure: airport({ longestRunwayFt: null }) })

    expect(text(container)).toContain('No runway data')

    unmount()
  })

  it('calls onClearDeparture when the departure selection is changed', async () => {
    const onClearDeparture = vi.fn()
    const { container, unmount } = await render({ departure: airport(), onClearDeparture })

    click(getByRole(container, 'button', { name: 'Change EGGD selection' }))

    expect(onClearDeparture).toHaveBeenCalledOnce()

    unmount()
  })

  it('calls onClearArrival when the arrival selection is changed, independently of departure', async () => {
    const onClearDeparture = vi.fn()
    const onClearArrival = vi.fn()
    const arrival = airport({ icao: 'EGPH', iata: 'EDI', name: 'Edinburgh Airport' })
    const { container, unmount } = await render({ departure: airport(), arrival, onClearDeparture, onClearArrival })

    click(getByRole(container, 'button', { name: 'Change EGPH selection' }))

    expect(onClearArrival).toHaveBeenCalledOnce()
    expect(onClearDeparture).not.toHaveBeenCalled()

    unmount()
  })
})

describe('AirportPickerCard - swap', () => {
  it('enables swap once at least one airport is picked, and calls onSwap when clicked', async () => {
    const onSwap = vi.fn()
    const { container, unmount } = await render({ departure: airport(), onSwap })

    const swapButton = getByRole(container, 'button', { name: 'Swap departure and arrival' })
    expect(isDisabled(swapButton)).toBe(false)

    click(swapButton)
    expect(onSwap).toHaveBeenCalledOnce()

    unmount()
  })
})
