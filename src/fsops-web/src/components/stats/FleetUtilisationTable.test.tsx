import { describe, expect, it } from 'vitest'

import { FleetUtilisationTable } from './FleetUtilisationTable'
import { mount, queryAllByRole, text } from '@/test/domHarness'
import type { StatsFleetAircraft } from '@/types/stats'

function aircraft(overrides: Partial<StatsFleetAircraft> = {}): StatsFleetAircraft {
  return {
    fleetAircraftId: 'ac-1',
    registration: 'G-ABCD',
    aircraftTypeName: 'A320-200',
    status: 'Active',
    sectorsFlown: 18,
    hoursFlownInPeriod: 42.5,
    idleHoursInPeriod: 677.5,
    utilisationPercent: 5.9,
    hoursSinceACheck: 320,
    hoursSinceCCheck: 1200,
    hoursToNextACheck: 180,
    hoursToNextCCheck: 2800,
    conditionPercent: 82.4,
    ...overrides,
  }
}

function render(status: 'loading' | 'ready' | 'error', rows: StatsFleetAircraft[], periodDays = 30) {
  return mount(<FleetUtilisationTable status={status} aircraft={rows} periodDays={periodDays} />)
}

describe('FleetUtilisationTable - states', () => {
  it('shows no rows while loading', async () => {
    const { container, unmount } = await render('loading', [])

    expect(queryAllByRole(container, 'row')).toHaveLength(0)
    expect(text(container)).not.toContain('No aircraft yet')

    unmount()
  })

  it('distinguishes a failed load from an empty fleet', async () => {
    const { container, unmount } = await render('error', [])

    expect(text(container)).toContain('Could not load fleet utilisation.')
    expect(text(container)).not.toContain('No aircraft yet')

    unmount()
  })

  it('says how to fill an empty fleet', async () => {
    const { container, unmount } = await render('ready', [])

    expect(text(container)).toContain('No aircraft yet')
    expect(text(container)).toContain('Buy or lease an aircraft')

    unmount()
  })

  it('says the hours are for the period, not the airframe lifetime', async () => {
    const { container, unmount } = await render('ready', [aircraft()], 30)

    // 42 hours flown and 12,000 airframe hours are wildly different claims; the caveat is what
    // stops the column being read as the second one.
    expect(text(container)).toContain('Last 30 days')
    expect(text(container)).toContain("not the airframe's lifetime total")

    unmount()
  })
})

describe('FleetUtilisationTable - the next check', () => {
  it('counts down to the A-check when that one falls due first', async () => {
    const { container, unmount } = await render('ready', [aircraft({ hoursToNextACheck: 180, hoursToNextCCheck: 2800 })])

    expect(text(container)).toContain('180h to A-check')

    unmount()
  })

  it('counts down to the C-check when that one falls due first', async () => {
    // Right after an A-check the C-check can genuinely be the nearer of the two. Always naming
    // the A-check would tell the player the wrong aircraft is about to go into the hangar, and
    // for the wrong length of time - an A-check and a C-check are not remotely the same downtime.
    const { container, unmount } = await render('ready', [aircraft({ hoursToNextACheck: 495, hoursToNextCCheck: 60 })])

    expect(text(container)).toContain('60h to C-check')
    expect(text(container)).not.toContain('to A-check')

    unmount()
  })

  it('picks the A-check when the two clocks are exactly level', async () => {
    const { container, unmount } = await render('ready', [aircraft({ hoursToNextACheck: 100, hoursToNextCCheck: 100 })])

    expect(text(container)).toContain('100h to A-check')

    unmount()
  })
})

describe('FleetUtilisationTable - rows', () => {
  it('shows each aircraft with its registration, type and flown hours', async () => {
    const { container, unmount } = await render('ready', [aircraft()])

    const body = text(container)
    expect(body).toContain('G-ABCD')
    expect(body).toContain('A320-200')
    expect(body).toContain('18')
    expect(body).toContain('42.5h')
    expect(body).toContain('677.5h')

    unmount()
  })

  it('lists one row per aircraft plus the header row', async () => {
    const { container, unmount } = await render('ready', [
      aircraft(),
      aircraft({ fleetAircraftId: 'ac-2', registration: 'G-EFGH' }),
    ])

    expect(queryAllByRole(container, 'row')).toHaveLength(3)

    unmount()
  })
})
