import { beforeEach, describe, expect, it, vi } from 'vitest'

import { FlightTrackCard } from './FlightTrackCard'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FlightTrack } from '@/types/flight'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

// maplibre-gl needs a real WebGL context, which jsdom does not have. The map itself is not what
// these tests are about - the empty states and the honesty of the surrounding copy are - so it is
// stubbed to a plain element and the assertions stay on the text a reader actually gets.
vi.mock('@/components/map/FlownTrackMap', () => ({
  FlownTrackMap: () => <div data-testid="flown-track-map" />,
  default: () => <div data-testid="flown-track-map" />,
}))

function track(overrides: Partial<FlightTrack> = {}): FlightTrack {
  return {
    flightId: 'flight-1',
    recordedPointCount: 3,
    thinned: false,
    points: [
      { utc: '2026-08-12T09:00:00Z', lat: 51.38, lon: -2.72, altMslFt: 1200, gsKt: 180, phase: 'Climb' },
      { utc: '2026-08-12T09:00:15Z', lat: 52.0, lon: -2.9, altMslFt: 24000, gsKt: 420, phase: 'Cruise' },
      { utc: '2026-08-12T09:00:30Z', lat: 55.95, lon: -3.37, altMslFt: 800, gsKt: 160, phase: 'Approach' },
    ],
    ...overrides,
  }
}

/** Answers the settings calls the provider makes, plus the track and airport lookups this card
 *  performs, and nothing else - an unexpected path fails loudly rather than resolving to undefined. */
function stubApi(trackResponse: FlightTrack) {
  vi.mocked(get).mockImplementation((path: string) => {
    const settings = settingsResponseFor(path)
    if (settings !== undefined) return Promise.resolve(settings) as never
    if (path.endsWith('/track')) return Promise.resolve(trackResponse) as never
    if (path.startsWith('/airports/')) {
      return Promise.resolve({ icao: path.split('/').pop(), name: 'Airport', latitude: 51.38, longitude: -2.72 }) as never
    }
    return Promise.reject(new Error(`Unexpected GET ${path}`)) as never
  })
}

async function render(trackResponse: FlightTrack) {
  stubApi(trackResponse)
  const mounted = await mount(
    <SettingsProvider>
      <FlightTrackCard flightId="flight-1" departureIcao="EGGD" arrivalIcao="EGPH" />
    </SettingsProvider>,
  )
  await flush()
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockReset()
})

describe('FlightTrackCard', () => {
  it('explains why there is no track rather than showing an empty map', async () => {
    // The normal case for every virtual-pilot sector and every flight that predates position
    // recording. It must read as an explanation, not as a failure.
    const { container, unmount } = await render(track({ recordedPointCount: 0, points: [] }))

    expect(text(document.body)).toContain('No track was recorded for this flight')
    expect(text(document.body)).toContain('there was no simulator attached to record from')
    expect(container.querySelector('[data-testid="flown-track-map"]')).toBeNull()

    unmount()
  })

  it('draws the track and names both lines once there is one', async () => {
    const { unmount } = await render(track())

    expect(document.body.querySelector('[data-testid="flown-track-map"]')).not.toBeNull()
    const body = text(document.body)
    expect(body).toContain('Flown')
    expect(body).toContain('Planned great circle')
    expect(body).toContain('3 positions recorded')

    unmount()
  })

  it('says plainly that a single position is not a path', async () => {
    const { unmount } = await render(
      track({
        recordedPointCount: 1,
        points: [{ utc: '2026-08-12T09:00:00Z', lat: 51.38, lon: -2.72, altMslFt: 300, gsKt: 0, phase: 'TaxiOut' }],
      }),
    )

    expect(text(document.body)).toContain('Only one position was ever recorded')
    expect(text(document.body)).toContain('1 position recorded')

    unmount()
  })

  it('discloses thinning, including that nothing recorded was changed or removed', async () => {
    // Thinning is a rendering decision. It must never be able to pass the reduced figure off as the
    // whole track.
    const { unmount } = await render(track({ recordedPointCount: 2880, thinned: true }))

    const body = text(document.body)
    expect(body).toContain('2,880 recorded')
    expect(body).toContain('The start and end are always kept')
    expect(body).toContain('nothing that was recorded has been changed or removed')

    unmount()
  })

  it('reports the highest recorded altitude', async () => {
    const { unmount } = await render(track())

    expect(text(document.body)).toContain('Highest 24,000 ft')

    unmount()
  })
})
