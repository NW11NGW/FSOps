import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'

import { FlightPlanImportBanner } from './FlightPlanImportBanner'
import { SettingsProvider } from '@/hooks/useSettings'
import type { FlightPlanImportStatus } from '@/hooks/useFlightPlanImport'
import { click, flush, getByRole, isDisabled, mount, queryAllByRole, text } from '@/test/domHarness'
import { TEST_CURRENCIES, TEST_SETTINGS } from '@/test/settingsStub'
import type { FlightPlanImport } from '@/types/flight'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function plan(overrides: Partial<FlightPlanImport> = {}): FlightPlanImport {
  return {
    available: true,
    source: 'SimBrief',
    fromSimBrief: true,
    message: null,
    blockFuelKg: 2200,
    cruiseAltitudeFt: 32000,
    blockTimeMinutes: 75,
    routeString: 'DCT',
    ...overrides,
  }
}

// Mutable so individual tests can flip it to null for the "not configured" case, matching how
// SettingsProvider actually resolves its /settings GET.
let pilotId: string | null = '555688'

beforeEach(() => {
  pilotId = '555688'
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') return { ...TEST_SETTINGS, simBriefPilotId: pilotId } as never
    if (path === '/settings/currencies') return TEST_CURRENCIES as never
    return undefined as never
  })
})

async function render(
  props: { planImport?: FlightPlanImport | null; status?: FlightPlanImportStatus; onRefresh?: () => void } = {},
) {
  const mounted = await mount(
    <MemoryRouter>
      <SettingsProvider>
        <FlightPlanImportBanner
          planImport={props.planImport === undefined ? null : props.planImport}
          status={props.status ?? 'idle'}
          onRefresh={props.onRefresh ?? vi.fn()}
        />
      </SettingsProvider>
    </MemoryRouter>,
  )
  await flush()
  return mounted
}

describe('FlightPlanImportBanner - Pilot ID not configured', () => {
  it('says so and points at Settings instead of showing nothing', async () => {
    pilotId = null
    const { container, unmount } = await render()
    expect(text(container)).toContain('Add your SimBrief Pilot ID in')
    expect(getByRole(container, 'link', { name: 'Settings' })).toBeTruthy()
    expect(queryAllByRole(container, 'button', { name: 'Check for OFP' })).toHaveLength(0)
    unmount()
  })
})

describe('FlightPlanImportBanner - idle and loading', () => {
  it('shows a neutral message before the first check has run', async () => {
    const { container, unmount } = await render({ status: 'idle' })
    expect(text(container)).toContain('Not checked yet.')
    unmount()
  })

  it('shows a loading skeleton on the very first check, with no plan yet to show', async () => {
    const { container, unmount } = await render({ status: 'loading', planImport: null })
    expect(container.querySelector('.animate-pulse')).toBeTruthy()
    expect(text(container)).not.toContain('Plan imported from SimBrief')
    unmount()
  })

  it('keeps the previous plan visible and disables the button while a refresh is in flight', async () => {
    const { container, unmount } = await render({ status: 'loading', planImport: plan() })
    expect(text(container)).toContain('Plan imported from SimBrief')
    const button = getByRole(container, 'button', { name: 'Checking…' })
    expect(isDisabled(button)).toBe(true)
    unmount()
  })
})

describe('FlightPlanImportBanner - the refresh button', () => {
  it('calls onRefresh when "Check for OFP" is clicked', async () => {
    const onRefresh = vi.fn()
    const { container, unmount } = await render({ status: 'success', planImport: plan(), onRefresh })

    click(getByRole(container, 'button', { name: 'Check for OFP' }))

    expect(onRefresh).toHaveBeenCalledTimes(1)
    unmount()
  })
})

describe('FlightPlanImportBanner - success from SimBrief', () => {
  it('shows the imported cruise level, block time, fuel and route', async () => {
    const { container, unmount } = await render({
      status: 'success',
      planImport: plan({ cruiseAltitudeFt: 36000, blockTimeMinutes: 90, blockFuelKg: 3000, routeString: 'DCT WAL DCT' }),
    })
    const body = text(container)
    expect(body).toContain('Plan imported from SimBrief.')
    expect(body).toContain('36,000 ft')
    expect(body).toContain('DCT WAL DCT')
    unmount()
  })
})

describe('FlightPlanImportBanner - SimBrief-side fallbacks (fail soft, never silent)', () => {
  it('explains when SimBrief has no plan on file for this Pilot ID', async () => {
    const { container, unmount } = await render({
      status: 'success',
      planImport: plan({
        fromSimBrief: false,
        message: 'SimBrief has no flight plan for this Pilot ID - using the built-in plan instead.',
      }),
    })
    const body = text(container)
    expect(body).toContain('Using the built-in plan.')
    expect(body).toContain('SimBrief has no flight plan for this Pilot ID')
    unmount()
  })

  it('explains when SimBrief could not be reached', async () => {
    const { container, unmount } = await render({
      status: 'success',
      planImport: plan({ fromSimBrief: false, message: 'Could not reach SimBrief - using the built-in plan instead.' }),
    })
    expect(text(container)).toContain('Could not reach SimBrief')
    unmount()
  })

  it('refuses a plan filed for a different city pair rather than importing it silently', async () => {
    const { container, unmount } = await render({
      status: 'success',
      planImport: plan({
        fromSimBrief: false,
        message: 'Your latest SimBrief plan is for EGLL-EGCC, not EGGD-EGPH - using the built-in plan instead.',
      }),
    })
    const body = text(container)
    expect(body).toContain('Using the built-in plan.')
    expect(body).toContain('Your latest SimBrief plan is for EGLL-EGCC, not EGGD-EGPH')
    unmount()
  })

  it('shows the message when there is nothing at all to plan against yet', async () => {
    const { container, unmount } = await render({
      status: 'success',
      planImport: plan({ available: false, fromSimBrief: false, message: 'No aircraft type is available to plan against yet.' }),
    })
    expect(text(container)).toContain('No aircraft type is available to plan against yet.')
    unmount()
  })
})

describe('FlightPlanImportBanner - FSOps itself unreachable', () => {
  it('is distinct from a SimBrief-side fallback, which is a successful response', async () => {
    const { container, unmount } = await render({ status: 'error', planImport: null })
    expect(text(container)).toContain('Could not reach FSOps to check for a plan.')
    unmount()
  })
})
