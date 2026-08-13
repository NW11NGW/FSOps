import { describe, expect, it, vi } from 'vitest'

import type { DraftLeg, OrphanedLeg } from './draftEntry'
import { OrphanedLegsDialog, type PendingRemoval } from './OrphanedLegsDialog'
import { click, findButton, getByRole, mount, queryByRole, text } from '@/test/domHarness'

function leg(overrides: Partial<DraftLeg> & { id: string; departureTimeUtc: string }): DraftLeg {
  return {
    routeId: 'route-1',
    departureIcao: 'EGLL',
    arrivalIcao: 'EGKK',
    flightNumber: 'FS100',
    blockMinutes: 60,
    isNew: false,
    ...overrides,
  }
}

const removedLeg = leg({ id: 'out', departureTimeUtc: '08:00:00', departureIcao: 'EGKK', arrivalIcao: 'EGPH', flightNumber: 'AGT501' })

describe('OrphanedLegsDialog', () => {
  it('renders nothing when there is no pending removal', async () => {
    const mounted = await mount(<OrphanedLegsDialog pending={null} onCancel={vi.fn()} onConfirm={vi.fn()} />)
    expect(queryByRole(document.body, 'heading', { name: /strands/ })).toBeNull()
    mounted.unmount()
  })

  it('names the single stranded leg, where the aircraft ends up instead, and offers to remove both', async () => {
    const orphan: OrphanedLeg = {
      day: 2,
      leg: leg({ id: 'ret', departureTimeUtc: '09:55:00', departureIcao: 'EGPH', arrivalIcao: 'EGKK', flightNumber: 'AGT502' }),
      aircraftActuallyAt: 'EGKK',
    }
    const pending: PendingRemoval = { day: 1, leg: removedLeg, orphans: [orphan] }
    const mounted = await mount(<OrphanedLegsDialog pending={pending} onCancel={vi.fn()} onConfirm={vi.fn()} />)

    // Singular wording for exactly one orphan.
    expect(getByRole(document.body, 'heading', { name: /This also strands another leg/ })).toBeTruthy()

    const body = text(document.body)
    expect(body).toContain('Tuesday')
    expect(body).toContain('09:55')
    expect(body).toContain('EGPH → EGKK')
    expect(body).toContain('AGT502')
    expect(body).toContain('stuck at EGKK instead')

    expect(findButton(document.body, 'Remove 2 legs')).toBeTruthy()

    mounted.unmount()
  })

  it('names every stranded leg and pluralises for more than one', async () => {
    const orphans: OrphanedLeg[] = [
      { day: 1, leg: leg({ id: 'c', departureTimeUtc: '12:00:00', departureIcao: 'EGPH', arrivalIcao: 'EGCC' }), aircraftActuallyAt: 'EGKK' },
      { day: 1, leg: leg({ id: 'd', departureTimeUtc: '14:00:00', departureIcao: 'EGCC', arrivalIcao: 'EGLL' }), aircraftActuallyAt: 'EGKK' },
    ]
    const pending: PendingRemoval = { day: 1, leg: removedLeg, orphans }
    const mounted = await mount(<OrphanedLegsDialog pending={pending} onCancel={vi.fn()} onConfirm={vi.fn()} />)

    expect(getByRole(document.body, 'heading', { name: /This also strands 2 other legs/ })).toBeTruthy()
    const body = text(document.body)
    expect(body).toContain('EGPH → EGCC')
    expect(body).toContain('EGCC → EGLL')
    expect(findButton(document.body, 'Remove 3 legs')).toBeTruthy()

    mounted.unmount()
  })

  it('cancelling calls onCancel and confirming calls onConfirm, neither touching the other', async () => {
    const orphan: OrphanedLeg = { day: 2, leg: leg({ id: 'ret', departureTimeUtc: '09:55:00' }), aircraftActuallyAt: 'EGKK' }
    const pending: PendingRemoval = { day: 1, leg: removedLeg, orphans: [orphan] }
    const onCancel = vi.fn()
    const onConfirm = vi.fn()
    const mounted = await mount(<OrphanedLegsDialog pending={pending} onCancel={onCancel} onConfirm={onConfirm} />)

    click(findButton(document.body, 'Cancel'))
    expect(onCancel).toHaveBeenCalledTimes(1)
    expect(onConfirm).not.toHaveBeenCalled()

    click(findButton(document.body, 'Remove 2 legs'))
    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onCancel).toHaveBeenCalledTimes(1)

    mounted.unmount()
  })
})
