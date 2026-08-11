import { describe, expect, it, vi } from 'vitest'

import { RenameAircraftDialog } from './RenameAircraftDialog'
import { mount, typeInto } from '@/test/domHarness'
import type { FleetAircraftSummary } from '@/types/fleet'

// RenameAircraftDialog only calls the API from event handlers (randomise/save), never on mount,
// so there is nothing to mock for these reset-behaviour tests - no SettingsProvider or /lib/api
// mock needed.

const aircraft: FleetAircraftSummary = {
  id: 'ac-1',
  registration: 'G-ABCD',
  aircraftTypeId: 'type-1',
  aircraftTypeName: 'A320-200',
  family: 'A320',
  paxCapacity: 180,
  ownership: 'Owned',
  status: 'Active',
  locationIcao: 'EGLL',
  airframeHours: 12000,
  hoursSinceACheck: 100,
  hoursSinceCCheck: 500,
  hoursToNextACheck: 400,
  hoursToNextCCheck: 4500,
  conditionPercent: 82,
  fuelOnBoardKg: 5000,
  groundedUntilUtc: null,
  groundedReason: null,
  reservedForPlayer: false,
  createdUtc: '2026-01-01T00:00:00Z',
}

describe('RenameAircraftDialog - reset behaviour', () => {
  it('does NOT wipe an in-progress edit when the parent re-renders with the same aircraft id', async () => {
    // Fleet.tsx builds `aircraft` as a fresh object literal on every render (e.g. from a live
    // cash-balance heartbeat elsewhere in the tree) - a reset effect keyed on the object itself
    // rather than its id would clear whatever the player just typed on every unrelated tick.
    const mounted = await mount(<RenameAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />)

    const input = document.body.querySelector<HTMLInputElement>('#rename-registration')
    if (!input) throw new Error('rename-registration input not found')
    typeInto(input, 'G-NEWXX')
    expect(input.value).toBe('G-NEWXX')

    await mounted.rerender(<RenameAircraftDialog aircraft={{ ...aircraft }} onOpenChange={vi.fn()} onSuccess={vi.fn()} />)

    expect(document.body.querySelector<HTMLInputElement>('#rename-registration')?.value).toBe('G-NEWXX')

    mounted.unmount()
  })

  it('DOES load the new registration when it opens for a genuinely different aircraft, discarding any unsaved edit', async () => {
    const mounted = await mount(<RenameAircraftDialog aircraft={aircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />)

    const input = document.body.querySelector<HTMLInputElement>('#rename-registration')
    if (!input) throw new Error('rename-registration input not found')
    typeInto(input, 'G-NEWXX')

    const otherAircraft: FleetAircraftSummary = { ...aircraft, id: 'ac-2', registration: 'N54321' }
    await mounted.rerender(<RenameAircraftDialog aircraft={otherAircraft} onOpenChange={vi.fn()} onSuccess={vi.fn()} />)

    expect(document.body.querySelector<HTMLInputElement>('#rename-registration')?.value).toBe('N54321')

    mounted.unmount()
  })
})
