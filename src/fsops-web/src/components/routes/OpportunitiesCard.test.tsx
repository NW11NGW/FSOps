import { beforeEach, describe, expect, it, vi } from 'vitest'

import { OpportunitiesCard } from './OpportunitiesCard'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, flush, mount, queryAllByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { OpportunitiesResponse, RouteOpportunity } from '@/types/planning'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function opportunity(overrides: Partial<RouteOpportunity> = {}): RouteOpportunity {
  return {
    departureIcao: 'EGGD',
    departureName: 'Bristol Airport',
    arrivalIcao: 'EGPH',
    arrivalName: 'Edinburgh Airport',
    arrivalMunicipality: 'Edinburgh',
    arrivalCountry: 'United Kingdom',
    distanceNm: 280,
    blockMinutes: 65,
    suggestedFare: 84,
    marketDemandPax: 245,
    expectedPassengers: 165,
    seats: 180,
    loadFactorPercent: 91.7,
    revenuePerSector: 13_860,
    costPerSector: 9_100,
    profitPerSector: 4_760,
    aircraftTypeName: 'Airbus A320',
    reason: 'EGGD-EGPH draws about 245 passengers a day, so an Airbus A320 sector fills to 92%.',
    ...overrides,
  }
}

function response(overrides: Partial<OpportunitiesResponse> = {}): OpportunitiesResponse {
  return {
    bases: ['EGGD'],
    fleetTypeCount: 1,
    opportunities: [opportunity()],
    blocked: [],
    ...overrides,
  }
}

async function render(data: OpportunitiesResponse | null, onPlan = vi.fn(), status: 'loading' | 'ready' | 'error' = 'ready') {
  const mounted = await mount(
    <SettingsProvider>
      <OpportunitiesCard data={data} status={status} isRefreshing={false} onPlan={onPlan} />
    </SettingsProvider>,
  )
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('OpportunitiesCard', () => {
  it('shows the pair, the aircraft it was priced with, and the reason it is being suggested', async () => {
    const { container, unmount } = await render(response())
    const body = text(container)

    expect(body).toContain('EGGD')
    expect(body).toContain('EGPH')
    expect(body).toContain('Airbus A320')
    expect(body).toContain('draws about 245 passengers a day')
    unmount()
  })

  /** Every recommendation needs a reason - a suggestion with no explanation is the black box this
   *  whole feature exists to remove. */
  it('never renders a suggestion without a reason', async () => {
    const { container, unmount } = await render(
      response({ opportunities: [opportunity(), opportunity({ arrivalIcao: 'EGPF', reason: 'Second reason here.' })] }),
    )
    const body = text(container)

    expect(body).toContain('draws about 245 passengers a day')
    expect(body).toContain('Second reason here.')
    unmount()
  })

  it('loads a suggestion into the planner rather than creating it outright', async () => {
    const onPlan = vi.fn()
    const { container, unmount } = await render(response(), onPlan)

    const planButton = queryAllByRole(container, 'button').find((b) => b.getAttribute('aria-label')?.startsWith('Plan '))
    expect(planButton).toBeDefined()
    click(planButton!)

    expect(onPlan).toHaveBeenCalledTimes(1)
    expect(onPlan.mock.calls[0]?.[0]).toMatchObject({ departureIcao: 'EGGD', arrivalIcao: 'EGPH' })
    unmount()
  })

  /** A pair the fleet cannot fly is stated plainly, not silently dropped - the same spirit as route
   *  creation's own refusals. */
  it('states pairs beyond the fleet instead of hiding them', async () => {
    const { container, unmount } = await render(
      response({
        opportunities: [],
        blocked: [
          {
            departureIcao: 'EGGD',
            arrivalIcao: 'KJFK',
            arrivalName: 'John F Kennedy International',
            arrivalCountry: 'United States',
            distanceNm: 2930,
            marketDemandPax: 410,
            reason: 'KJFK would carry about 410 passengers a day from EGGD, but 2,930 nm is beyond every aircraft you own.',
          },
        ],
      }),
    )

    expect(text(container)).toContain('beyond every aircraft you own')
    unmount()
  })

  it('tells a fleetless airline to get an aircraft rather than showing an empty list', async () => {
    const { container, unmount } = await render(response({ fleetTypeCount: 0, opportunities: [], blocked: [] }))
    expect(text(container)).toContain('Lease or buy an aircraft first')
    unmount()
  })

  it('surfaces a failure rather than pretending there is nothing to suggest', async () => {
    const { container, unmount } = await render(null, vi.fn(), 'error')
    expect(text(container)).toContain('Could not work out any suggestions')
    unmount()
  })
})
