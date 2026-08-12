import { beforeEach, describe, expect, it, vi } from 'vitest'

import { LeasesSection } from './LeasesSection'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, flush, getByRole, mount, queryAllByRole, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { FinanceLease } from '@/types/finance'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function lease(overrides: Partial<FinanceLease> = {}): FinanceLease {
  return {
    leaseId: 'lease-1',
    fleetAircraftId: 'ac-1',
    registration: 'G-ABCD',
    aircraftTypeName: 'A320-200',
    monthlyRate: 42000,
    startUtc: '2026-03-04T00:00:00Z',
    nextPaymentUtc: '2026-08-31T00:00:00Z',
    ...overrides,
  }
}

async function render(
  status: 'loading' | 'ready' | 'error',
  leases: FinanceLease[],
  totalMonthlyCommitment = 0,
  onEndLease = vi.fn(),
) {
  const mounted = await mount(
    <SettingsProvider>
      <LeasesSection
        status={status}
        leases={leases}
        totalMonthlyCommitment={totalMonthlyCommitment}
        onEndLease={onEndLease}
      />
    </SettingsProvider>,
  )
  await flush()
  return { ...mounted, onEndLease }
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
})

describe('LeasesSection - states', () => {
  it('shows no lease rows and no empty message while loading', async () => {
    const { container, unmount } = await render('loading', [])

    expect(queryAllByRole(container, 'row')).toHaveLength(0)
    expect(text(container)).not.toContain('No active leases')

    unmount()
  })

  it('distinguishes a failed load from having no leases', async () => {
    const { container, unmount } = await render('error', [])

    expect(text(container)).toContain('Could not load your leases.')
    expect(text(container)).not.toContain('No active leases')

    unmount()
  })

  it('explains where leases come from when there are none', async () => {
    const { container, unmount } = await render('ready', [])

    expect(text(container)).toContain('No active leases')
    expect(text(container)).toContain('Lease an aircraft from the Fleet page')

    unmount()
  })
})

describe('LeasesSection - active leases', () => {
  it('shows each aircraft, its type and its monthly rate', async () => {
    const { container, unmount } = await render('ready', [lease()], 42000)

    const body = text(container)
    expect(body).toContain('G-ABCD')
    expect(body).toContain('A320-200')
    expect(body).toContain('$42,000.00')

    unmount()
  })

  it('shows the next payment as a real date, never as a day of the month', async () => {
    const { container, unmount } = await render('ready', [lease({ nextPaymentUtc: '2026-08-31T00:00:00Z' })], 42000)

    // Billing runs on a rolling 30-day cycle from the airline's own clock, so implying "the 1st"
    // or "monthly on the 4th" would be a straightforwardly false statement about when money moves.
    expect(text(container)).toContain('31 Aug 2026')

    unmount()
  })

  it('totals the monthly commitment across every lease', async () => {
    const { container, unmount } = await render(
      'ready',
      [lease(), lease({ leaseId: 'lease-2', registration: 'G-EFGH', monthlyRate: 38000 })],
      80000,
    )

    const body = text(container)
    expect(body).toContain('Total monthly commitment')
    expect(body).toContain('$80,000.00')

    unmount()
  })

  it('shows no total when there is nothing leased, rather than a commitment of zero', async () => {
    const { container, unmount } = await render('ready', [], 0)

    expect(text(container)).not.toContain('Total monthly commitment')

    unmount()
  })

  it('hands the whole lease back when its exit is chosen', async () => {
    const active = lease()
    const { container, onEndLease, unmount } = await render('ready', [active], 42000)

    click(getByRole(container, 'button', { name: 'End lease' }))

    expect(onEndLease).toHaveBeenCalledWith(active)

    unmount()
  })

  it('offers an exit per lease, not one for the whole list', async () => {
    const { container, unmount } = await render(
      'ready',
      [lease(), lease({ leaseId: 'lease-2', registration: 'G-EFGH' })],
      80000,
    )

    expect(queryAllByRole(container, 'button', { name: 'End lease' })).toHaveLength(2)

    unmount()
  })
})
