import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CopyDayDialog } from './CopyDayDialog'
import type { DraftLeg, DraftWeek } from './draftEntry'
import { click, findButton, flush, mount, queryByRole, text } from '@/test/domHarness'

function leg(overrides: Partial<DraftLeg> & { id: string; departureTimeUtc: string }): DraftLeg {
  return {
    routeId: 'route-out',
    departureIcao: 'EGGD',
    arrivalIcao: 'EGPH',
    flightNumber: 'FS100',
    blockMinutes: 65,
    isNew: false,
    ...overrides,
  }
}

/** Monday is a two-leg out-and-back; Wednesday already has a hand-built day on it. */
const week: DraftWeek = {
  1: {
    dayOfWeek: 1,
    fleetAircraftId: 'ac-1',
    registration: 'G-WJQG',
    legs: [
      leg({ id: 'mon-out', departureTimeUtc: '06:00:00' }),
      leg({ id: 'mon-back', departureTimeUtc: '10:00:00', routeId: 'route-back', departureIcao: 'EGPH', arrivalIcao: 'EGGD' }),
    ],
  },
  3: {
    dayOfWeek: 3,
    fleetAircraftId: 'ac-1',
    registration: 'G-WJQG',
    legs: [leg({ id: 'wed-out', departureTimeUtc: '07:00:00' })],
  },
}

/** The dialog asks the backend what a paste would do; these are the two answers it can get. */
function stubPreview(body: unknown, ok = true) {
  return vi.fn().mockResolvedValue({
    ok,
    json: async () => body,
  } as unknown as Response)
}

/** Lets the pending preview request resolve and React re-render before assertions. Two passes: one
 *  for the fetch promise, one for the state update it schedules. */
async function settle() {
  await flush()
  await flush()
}

describe('CopyDayDialog', () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    vi.useRealTimers()
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
    vi.restoreAllMocks()
  })

  it('renders nothing until a day is being copied', async () => {
    const mounted = await mount(
      <CopyDayDialog sourceDay={null} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={vi.fn()} />,
    )
    expect(queryByRole(document.body, 'heading', { name: /Copy/ })).toBeNull()
    mounted.unmount()
  })

  it('says what is being copied - every leg and the aircraft, because a duty day flies one airframe', async () => {
    globalThis.fetch = stubPreview({ isValid: true, conflicts: [], advisories: [] })
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={vi.fn()} />,
    )

    const body = text(document.body)
    expect(body).toContain('Copy Monday to other days')
    expect(body).toContain('2 legs')
    expect(body).toContain('G-WJQG')

    mounted.unmount()
  })

  it('confirms a clean copy in the backend\'s own words, and hands back a week with the day on every target', async () => {
    globalThis.fetch = stubPreview({ isValid: true, conflicts: [], advisories: [] })
    const onConfirm = vi.fn()
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={onConfirm} />,
    )

    click(findButton(document.body, 'Tue'))
    await settle()

    expect(text(document.body)).toContain('This copy works')

    click(findButton(document.body, 'Copy to 1 day'))
    const next = onConfirm.mock.calls[0]?.[0] as DraftWeek
    expect(next[2]?.legs.map((l) => l.departureTimeUtc)).toEqual(['06:00:00', '10:00:00'])
    expect(next[2]?.fleetAircraftId).toBe('ac-1')
    // Fresh ids - two days sharing one would make the grid treat them as the same block.
    expect(next[2]?.legs.map((l) => l.id)).not.toEqual(['mon-out', 'mon-back'])
    // The source is untouched.
    expect(next[1]?.legs.map((l) => l.id)).toEqual(['mon-out', 'mon-back'])

    mounted.unmount()
  })

  it('shows exactly what a paste would break, in the backend\'s sentence, and never refuses silently', async () => {
    globalThis.fetch = stubPreview({
      isValid: false,
      conflicts: ['G-WJQG lands at LFPG (Tuesday 19:34) but its next leg departs EGGD (Wednesday 06:00) - schedule a LFPG -> EGGD leg before this one to reposition it.'],
      advisories: [],
    })
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={vi.fn()} />,
    )

    click(findButton(document.body, 'Tue'))
    await settle()

    const body = text(document.body)
    expect(body).toContain('This copy breaks one thing')
    // The specific obstacle, named - never a generic "that would not work".
    expect(body).toContain('G-WJQG lands at LFPG')
    expect(body).toContain('departs EGGD')
    expect(body).toContain('will not save until')

    // Still the player's decision, not ours.
    expect(findButton(document.body, 'Copy anyway')).toBeTruthy()

    mounted.unmount()
  })

  it('warns before it overwrites a day that already has legs, and says the copy is undoable', async () => {
    globalThis.fetch = stubPreview({ isValid: true, conflicts: [], advisories: [] })
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={vi.fn()} />,
    )

    click(findButton(document.body, 'Wed'))
    await settle()

    const body = text(document.body)
    expect(body).toContain('This replaces what is already there')
    expect(body).toContain('Wednesday (1 leg)')
    expect(body).toContain('Discard changes puts it back')
    // The verb changes too - "Copy" would understate what the button does.
    expect(findButton(document.body, 'Replace 1 day')).toBeTruthy()

    mounted.unmount()
  })

  it('never presents an unasked question as a clean answer when the check could not be made', async () => {
    globalThis.fetch = vi.fn().mockRejectedValue(new Error('offline'))
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={vi.fn()} onConfirm={vi.fn()} />,
    )

    click(findButton(document.body, 'Tue'))
    await settle()

    const body = text(document.body)
    expect(body).toContain('Could not check this copy')
    expect(body).not.toContain('This copy works')

    mounted.unmount()
  })

  it('cancelling changes nothing', async () => {
    globalThis.fetch = stubPreview({ isValid: true, conflicts: [], advisories: [] })
    const onClose = vi.fn()
    const onConfirm = vi.fn()
    const mounted = await mount(
      <CopyDayDialog sourceDay={1} week={week} pilotId="p1" onClose={onClose} onConfirm={onConfirm} />,
    )

    click(findButton(document.body, 'Tue'))
    await settle()
    click(findButton(document.body, 'Cancel'))

    expect(onClose).toHaveBeenCalledTimes(1)
    expect(onConfirm).not.toHaveBeenCalled()

    mounted.unmount()
  })
})
