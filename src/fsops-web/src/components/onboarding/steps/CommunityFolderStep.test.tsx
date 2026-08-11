import { act } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { click, flush, mount } from '@/test/domHarness'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post } from '@/lib/api'
import { CommunityFolderStep } from './CommunityFolderStep'
import { DEFAULT_WIZARD_DATA, type WizardData } from '../wizardData'

/** Waits past the step's 350ms validate debounce. Wrapped in act() because the debounced
 *  setTimeout callback triggers a React state update outside of any event handler React is
 *  already tracking. */
async function waitForDebounce() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 450))
  })
}

beforeEach(() => {
  vi.mocked(get).mockReset()
  vi.mocked(post).mockReset()
})

describe('CommunityFolderStep - detection', () => {
  it('shows a detected candidate and lets the player select it', async () => {
    vi.mocked(get).mockResolvedValue([
      { path: 'C:\\MSFS\\Packages\\Community', source: 'UserCfg.opt (Steam / non-Store install)', exists: true },
    ])

    const onChange = vi.fn()
    const mounted = await mount(<CommunityFolderStep data={DEFAULT_WIZARD_DATA} onChange={onChange} />)
    await flush()

    expect(get).toHaveBeenCalledWith('/panel/detect')
    const card = mounted.container.querySelector('button[aria-pressed]')
    expect(card).toBeTruthy()
    expect(card?.textContent).toContain('C:\\MSFS\\Packages\\Community')

    click(card!)
    expect(onChange).toHaveBeenCalledWith({ communityFolderPath: 'C:\\MSFS\\Packages\\Community' })

    mounted.unmount()
  })

  it('shows an honest "nothing found" state rather than an empty box when detection finds nothing', async () => {
    vi.mocked(get).mockResolvedValue([])

    const mounted = await mount(<CommunityFolderStep data={DEFAULT_WIZARD_DATA} onChange={vi.fn()} />)
    await flush()

    expect(mounted.container.textContent).toContain('No MSFS 2024 install was found')

    mounted.unmount()
  })

  it('does not block the step on a detection failure - manual entry stays available', async () => {
    vi.mocked(get).mockRejectedValue(new Error('network down'))

    const mounted = await mount(<CommunityFolderStep data={DEFAULT_WIZARD_DATA} onChange={vi.fn()} />)
    await flush()

    expect(mounted.container.textContent).toContain("Couldn\u2019t scan for MSFS automatically")
    expect(mounted.container.querySelector('#community-folder-path')).toBeTruthy()

    mounted.unmount()
  })
})

describe('CommunityFolderStep - manual path validation', () => {
  it('validates a manually-entered path and surfaces a refusal reason', async () => {
    vi.mocked(get).mockResolvedValue([])
    vi.mocked(post).mockResolvedValue({ valid: false, reason: 'This looks like "Packages", not a Community folder.', resolvedPath: null })

    const data: WizardData = { ...DEFAULT_WIZARD_DATA, communityFolderPath: 'C:\\MSFS\\Packages' }
    const mounted = await mount(<CommunityFolderStep data={data} onChange={vi.fn()} />)
    await flush()
    await waitForDebounce()
    await flush()

    expect(post).toHaveBeenCalledWith('/panel/validate', { path: 'C:\\MSFS\\Packages' })
    expect(mounted.container.textContent).toContain('not a Community folder')

    mounted.unmount()
  })
})

describe('CommunityFolderStep - skipping', () => {
  it('is fully skippable: an empty path shows no error and offers to leave it blank', async () => {
    vi.mocked(get).mockResolvedValue([])

    const mounted = await mount(<CommunityFolderStep data={DEFAULT_WIZARD_DATA} onChange={vi.fn()} />)
    await flush()

    expect(mounted.container.textContent).toContain('Leave this blank to skip')
    // No "Skip this for now" button should be shown when there's nothing to skip.
    expect(Array.from(mounted.container.querySelectorAll('button')).some((b) => b.textContent?.includes('Skip this for now'))).toBe(false)

    mounted.unmount()
  })

  it('"Skip this for now" clears an already-entered path', async () => {
    vi.mocked(get).mockResolvedValue([])
    vi.mocked(post).mockResolvedValue({ valid: true, reason: null, resolvedPath: 'C:\\MSFS\\Packages\\Community' })

    const data: WizardData = { ...DEFAULT_WIZARD_DATA, communityFolderPath: 'C:\\MSFS\\Packages\\Community' }
    const onChange = vi.fn()
    const mounted = await mount(<CommunityFolderStep data={data} onChange={onChange} />)
    await flush()

    const skipButton = Array.from(mounted.container.querySelectorAll('button')).find((b) => b.textContent?.includes('Skip this for now'))
    expect(skipButton).toBeTruthy()
    click(skipButton!)

    expect(onChange).toHaveBeenCalledWith({ communityFolderPath: null })

    mounted.unmount()
  })
})
