import { act } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { DraftWeek } from './draftEntry'
import { LegDialog } from './LegDialog'
import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { LegOptionsResponse } from '@/types/schedule'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

vi.mock('@/hooks/useSchedule', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/hooks/useSchedule')>()
  return { ...actual, fetchAircraftOptions: vi.fn(), fetchLegOptions: vi.fn() }
})

import { get } from '@/lib/api'
import { fetchAircraftOptions, fetchLegOptions } from '@/hooks/useSchedule'

/** Waits past LegDialog's 250ms leg-options debounce. Wrapped in act() because the debounced
 *  setTimeout callback triggers a React state update outside of any event handler React is
 *  already tracking. */
async function waitForDebounce() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 400))
  })
}

const week: DraftWeek = {
  1: { dayOfWeek: 1, fleetAircraftId: 'ac-1', registration: 'G-GEJU', legs: [] },
}

async function renderLegStep(legOptions: LegOptionsResponse) {
  // LegDialog's aircraft-step effect and its "reset on open" effect both run off the SAME initial
  // commit (step still reads its useState default of 'aircraft' until the reset effect's setStep
  // takes hold), so fetchAircraftOptions fires transiently on mount even though this dialog is
  // going straight to the leg step (week[day] is already populated below) - give it a benign
  // response so that stray call doesn't reject/crash the mount.
  vi.mocked(fetchAircraftOptions).mockResolvedValue({ options: [] })
  vi.mocked(fetchLegOptions).mockResolvedValue(legOptions)
  const mounted = await mount(
    <SettingsProvider>
      <LegDialog
        open
        onOpenChange={vi.fn()}
        pilotId="pilot-1"
        mode="add"
        day={1}
        initialTime="13:35"
        week={week}
        onSetAircraft={vi.fn()}
        onConfirmAdd={vi.fn()}
        onConfirmRetime={vi.fn()}
        onRemove={vi.fn()}
      />
    </SettingsProvider>,
  )
  await flush()
  await waitForDebounce()
  await flush()
  return mounted
}

beforeEach(() => {
  vi.mocked(get).mockImplementation(async (path: string) => settingsResponseFor(path) as never)
  vi.mocked(fetchLegOptions).mockReset()
})

describe('LegDialog - forward continuity gap warning', () => {
  it('phrases the gap as a consequence and a next step - where the aircraft ends up, and a leg to add AFTER this one, never the validator\'s own "before" wording', async () => {
    // Reproduces the user's own real-airline shape: G-GEJU at EGKK, EGKK->EGPH offered for a
    // Monday 13:35 slot, with a Tuesday 08:00 EGKK departure already on the calendar. Picking it
    // leaves the aircraft at EGPH, which is exactly the continuity gap this fix concerns.
    const legOptions: LegOptionsResponse = {
      legal: [
        {
          routeId: 'route-501',
          departureIcao: 'EGKK',
          arrivalIcao: 'EGPH',
          flightNumber: '501',
          blockMinutes: 81,
          warnings: [
            {
              message:
                'Leaves G-GEJU at EGPH. Its next leg departs EGKK (Tuesday at 08:00) - add a EGPH -> EGKK leg after this one.',
              severity: 'info',
            },
          ],
        },
      ],
      illegal: [],
      aircraftPosition: 'EGKK',
    }

    const mounted = await renderLegStep(legOptions)

    const body = text(document.body)
    expect(body).toContain('EGPH')
    expect(body).toContain('EGKK')
    expect(body).toContain('add a EGPH -> EGKK leg after this one')
    expect(body).not.toContain('before this one')

    mounted.unmount()
  })

  it('still shows an alert-severity warning (a genuine incompatibility) for double-booking, unrelated to the continuity-gap case', async () => {
    const legOptions: LegOptionsResponse = {
      legal: [
        {
          routeId: 'route-502',
          departureIcao: 'EGPH',
          arrivalIcao: 'EGKK',
          flightNumber: '502',
          blockMinutes: 81,
          warnings: [
            {
              message: 'G-GEJU is double-booked: the leg landing at Monday 14:56 overlaps the one departing Monday 15:00.',
              severity: 'alert',
            },
          ],
        },
      ],
      illegal: [],
      aircraftPosition: 'EGKK',
    }

    const mounted = await renderLegStep(legOptions)

    const body = text(document.body)
    expect(body).toContain('double-booked')

    mounted.unmount()
  })

  it('renders no warning row at all for an option with no consequences', async () => {
    const legOptions: LegOptionsResponse = {
      legal: [
        { routeId: 'route-501', departureIcao: 'EGKK', arrivalIcao: 'EGPH', flightNumber: '501', blockMinutes: 81, warnings: [] },
      ],
      illegal: [],
      aircraftPosition: 'EGKK',
    }

    const mounted = await renderLegStep(legOptions)

    const body = text(document.body)
    expect(body).toContain('EGKK → EGPH')
    expect(body).not.toContain('add a')
    expect(body).not.toContain('double-booked')

    mounted.unmount()
  })
})
