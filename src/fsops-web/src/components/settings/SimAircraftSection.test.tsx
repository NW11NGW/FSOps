import { beforeEach, describe, expect, it, vi } from 'vitest'

import { SimAircraftSection } from './SimAircraftSection'
import { click, findButton, flush, getByRole, mount, text } from '@/test/domHarness'
import type { SimAircraftEntry, SimAircraftScan, SimAircraftState } from '@/types/simAircraft'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post, put } from '@/lib/api'

function aircraft(overrides: Partial<SimAircraftEntry> = {}): SimAircraftEntry {
  return {
    typeDesignator: 'C172',
    name: 'Cessna 172 Skyhawk',
    manufacturer: 'Cessna',
    category: 'LightSingle',
    seats: 3,
    payloadKg: 340,
    rangeNm: 640,
    cruiseTasKts: 122,
    shipsWith: 'Standard',
    available: true,
    evidence: 'Edition',
    ...overrides,
  }
}

function state(overrides: Partial<SimAircraftState> = {}): SimAircraftState {
  return {
    // Standard, matching what the server returns for an install that has stored nothing. Worth
    // being deliberate about: a stub defaulting to Premium Deluxe would let a regression in the
    // real default pass every test in this file.
    edition: 'Standard',
    configuredCommunityFolderPath: null,
    effectiveCommunityFolderPath: null,
    lastScan: null,
    aircraft: [aircraft()],
    ...overrides,
  }
}

function scan(overrides: Partial<SimAircraftScan> = {}): SimAircraftScan {
  return {
    outcome: 'Scanned',
    communityFolderPath: 'D:\\MSFS\\Community',
    scannedUtc: '2026-08-20T12:00:00+00:00',
    packagesInspected: 33,
    aircraftPackages: [],
    basePackageTypeDesignators: [],
    ...overrides,
  }
}

async function render(initial: SimAircraftState) {
  vi.mocked(get).mockResolvedValue(initial)
  return mount(<SimAircraftSection />)
}

describe('SimAircraftSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows a brand-new player their edition and tells them nothing has been scanned', async () => {
    const { container, unmount } = await render(state())

    expect(text(container)).toContain('Nothing scanned yet')
    expect(getByRole(container, 'button', { name: 'Standard' }).getAttribute('aria-checked')).toBe('true')
    expect(getByRole(container, 'button', { name: 'Premium Deluxe' }).getAttribute('aria-checked')).toBe('false')

    unmount()
  })

  /**
   * The claim that matters most on this screen. A scan is evidence, never a verdict - MSFS streams
   * most of its aircraft, so a folder that could not be read says nothing at all about what the
   * player owns, and the copy has to say so where they are looking.
   */
  it('says a failed scan has taken nothing away', async () => {
    const { container, unmount } = await render(
      state({ lastScan: scan({ outcome: 'FolderMissing' }) }),
    )

    expect(text(container)).toContain('That folder is not there any more')
    expect(text(container)).toContain('Nothing has been taken away')

    unmount()
  })

  it('explains the wrong folder rather than reporting an empty hangar', async () => {
    const { container, unmount } = await render(
      state({ lastScan: scan({ outcome: 'NotAPackagesFolder' }) }),
    )

    expect(text(container)).toContain('does not look like a Community folder')

    unmount()
  })

  it('reports what a scan found, and invites the player to add the rest', async () => {
    const { container, unmount } = await render(
      state({
        lastScan: scan({
          aircraftPackages: [
            { packageFolder: 'fnx-aircraft-320', packageTitle: 'Fenix Airbus A320', rawDesignator: 'A320', typeDesignator: 'A320' },
            { packageFolder: 'fsltl-traffic-base', packageTitle: 'FSLTL Traffic Base', rawDesignator: null, typeDesignator: null },
          ],
          basePackageTypeDesignators: ['C172'],
        }),
        aircraft: [aircraft(), aircraft({ typeDesignator: 'A320', name: 'Airbus A320', category: 'Narrowbody', shipsWith: 'AddOn', evidence: 'CommunityFolder' })],
      }),
    )

    const rendered = text(container)
    expect(rendered).toContain('Fenix Airbus A320')
    expect(rendered).toContain('Found in Community')
    expect(rendered).toContain('Tick anything else you have')
    expect(rendered).toContain('1 aircraft package was not recognised')

    unmount()
  })

  it('scans on demand and shows what came back', async () => {
    const { container, unmount } = await render(state())
    vi.mocked(post).mockResolvedValue(state({ lastScan: scan({ packagesInspected: 12 }) }))

    click(findButton(container, 'Scan for aircraft'))
    await flush()

    expect(vi.mocked(post)).toHaveBeenCalledWith('/sim-aircraft/scan')
    expect(text(container)).toContain('Looked at 12 packages')

    unmount()
  })

  it('changing the edition saves it', async () => {
    const { container, unmount } = await render(state())
    vi.mocked(put).mockResolvedValue(state({ edition: 'Deluxe' }))

    click(getByRole(container, 'button', { name: 'Deluxe' }))
    await flush()

    expect(vi.mocked(put)).toHaveBeenCalledWith('/sim-aircraft', {
      edition: 'Deluxe',
      clearCommunityFolderPath: false,
    })

    unmount()
  })

  /**
   * The player overrules FSOps, and that is the whole point of the tick list. Unticking something
   * FSOps believes they have sends `false`; clicking a row they already overrode clears the
   * override rather than stacking a second one on top of it.
   */
  it('lets the player untick an aircraft FSOps thinks they have, and clear the tick again', async () => {
    const { container, unmount } = await render(state())
    vi.mocked(put).mockResolvedValue(state({ aircraft: [aircraft({ available: false, evidence: 'TickedOff' })] }))

    click(getByRole(container, 'button', { name: 'Cessna 172 Skyhawk' }))
    await flush()

    expect(vi.mocked(put)).toHaveBeenCalledWith('/sim-aircraft/C172', { available: false })
    expect(text(container)).toContain('You removed it')

    vi.mocked(put).mockResolvedValue(state())
    click(getByRole(container, 'button', { name: 'Cessna 172 Skyhawk' }))
    await flush()

    expect(vi.mocked(put)).toHaveBeenLastCalledWith('/sim-aircraft/C172', { available: null })

    unmount()
  })

  it('offers to try again when the settings cannot be read at all', async () => {
    vi.mocked(get).mockRejectedValue(new Error('offline'))
    const { container, unmount } = await mount(<SimAircraftSection />)
    await flush()

    expect(text(container)).toContain('Could not read which aircraft you have')
    expect(findButton(container, 'Try again')).toBeTruthy()

    unmount()
  })
})
