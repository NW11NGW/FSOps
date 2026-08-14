import { beforeEach, describe, expect, it, vi } from 'vitest'

import { UpdatesSection } from './UpdatesSection'
import { click, flush, mount } from '@/test/domHarness'
import type { UpdateStatus } from '@/lib/updateApi'

/** domHarness.findButton throws when nothing matches, which is the wrong shape for the several
 *  assertions here that a button is deliberately absent. */
function queryButton(root: ParentNode, text: string): HTMLButtonElement | null {
  return Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.includes(text)) ?? null
}

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get, post, put } from '@/lib/api'

function status(overrides: Partial<UpdateStatus> = {}): UpdateStatus {
  return {
    enabled: true,
    checking: false,
    currentVersion: '0.1.0',
    latestVersion: null,
    updateAvailable: false,
    dismissed: false,
    lastCheckedUtc: '2026-08-11T12:00:00+00:00',
    lastCheckFailed: false,
    releaseUrl: null,
    releaseNotes: null,
    releasePublishedUtc: null,
    downloadAvailable: false,
    downloadState: 'none',
    downloadFileName: null,
    downloadSha256: null,
    downloadedBytes: 0,
    downloadMessage: null,
    // Stable, matching what the server returns for an install that has stored nothing. Worth being
    // deliberate about: a stub that defaulted to development would let a regression in the real
    // default pass every test in this file.
    channel: 'stable',
    aheadOfChannel: false,
    channelNewestVersion: null,
    ...overrides,
  }
}

const AVAILABLE: Partial<UpdateStatus> = {
  latestVersion: '0.2.0',
  updateAvailable: true,
  releaseUrl: 'https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0',
  releaseNotes: 'Fixed the thing.',
  releasePublishedUtc: '2026-08-10T09:00:00+00:00',
  downloadAvailable: true,
}

async function render(initial: UpdateStatus) {
  vi.mocked(get).mockResolvedValue(initial)
  return mount(<UpdatesSection />)
}

describe('UpdatesSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows the running version and says so when there is nothing newer', async () => {
    const { container, unmount } = await render(status())

    expect(container.textContent).toContain('0.1.0')
    expect(container.textContent).toContain('You are on the latest version.')
    unmount()
  })

  it('offers the update, its notes and a link to the release when one exists', async () => {
    const { container, unmount } = await render(status(AVAILABLE))

    expect(container.textContent).toContain('0.2.0')
    expect(container.textContent).toContain('Fixed the thing.')

    const link = container.querySelector('a[href="https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0"]')
    expect(link).not.toBeNull()
    expect(link?.getAttribute('rel')).toContain('noopener')
    unmount()
  })

  it('turning the check off says plainly that nothing leaves the machine, and offers no download', async () => {
    vi.mocked(put).mockResolvedValue(status({ enabled: false, ...AVAILABLE }))
    const { container, unmount } = await render(status(AVAILABLE))

    const off = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Off')
    expect(off).toBeDefined()
    click(off!)
    await flush()

    expect(vi.mocked(put)).toHaveBeenCalledWith('/update/preferences', { enabled: false })
    expect(container.textContent).toContain('will not contact anything outside your machine')
    expect(queryButton(container, 'Download and verify')).toBeNull()
    unmount()
  })

  it('a release with no checksum is announced but never offered as a download', async () => {
    // The supply-chain rule, visible in the UI: an unsigned installer with nothing to verify it
    // against is not something the app will fetch on the user's behalf.
    const { container, unmount } = await render(status({ ...AVAILABLE, downloadAvailable: false }))

    expect(container.textContent).toContain('0.2.0')
    expect(container.textContent).toContain('does not publish an installer with a checksum')
    expect(queryButton(container, 'Download and verify')).toBeNull()
    unmount()
  })

  it('starting a download posts once and shows progress rather than a frozen button', async () => {
    vi.mocked(post).mockResolvedValue(status({ ...AVAILABLE, downloadState: 'downloading', downloadedBytes: 5_242_880 }))
    const { container, unmount } = await render(status(AVAILABLE))

    const button = queryButton(container, 'Download and verify')
    expect(button).not.toBeNull()
    click(button!)
    await flush()

    expect(vi.mocked(post)).toHaveBeenCalledWith('/update/download')
    expect(container.textContent).toContain('Downloading')
    expect(container.textContent).toContain('5.0 MB')
    unmount()
  })

  it('a verified download shows its checksum and says the app will not run the installer', async () => {
    const { container, unmount } = await render(
      status({
        ...AVAILABLE,
        downloadState: 'ready',
        downloadFileName: 'FSOps-Setup-0.2.0.exe',
        downloadSha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      }),
    )

    expect(container.textContent).toContain('SHA-256 verified')
    expect(container.textContent).toContain('FSOps-Setup-0.2.0.exe')
    expect(container.textContent).toContain('will not run the installer for you')

    // The full hash stays available for anyone comparing it against the release page by eye.
    const hashLine = container.querySelector('[title="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"]')
    expect(hashLine).not.toBeNull()
    unmount()
  })

  it('a failed verification is shown to the user, and no installer is named', async () => {
    const { container, unmount } = await render(
      status({
        ...AVAILABLE,
        downloadState: 'failed',
        downloadMessage: 'The downloaded file did not match the release checksum, so it was deleted.',
      }),
    )

    expect(container.textContent).toContain('did not match')
    expect(container.textContent).not.toContain('SHA-256 verified')
    expect(queryButton(container, 'Show the installer')).toBeNull()
    unmount()
  })

  it('defaults to the stable channel and says plainly what it means', async () => {
    const { container, unmount } = await render(status())

    const stable = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Stable')
    const development = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Development')
    expect(stable?.getAttribute('aria-checked')).toBe('true')
    expect(development?.getAttribute('aria-checked')).toBe('false')

    expect(container.textContent).toContain('Finished, released versions only')
    unmount()
  })

  it('does not warn about development builds while stable is selected', async () => {
    // A warning that is always on screen is a warning nobody reads.
    const { container, unmount } = await render(status())

    expect(container.textContent).not.toContain('not finished software')
    unmount()
  })

  it('choosing development states the cost beside the control, before anything is downloaded', async () => {
    vi.mocked(put).mockResolvedValue(status({ channel: 'development' }))
    const { container, unmount } = await render(status())

    const development = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Development')
    expect(development).toBeDefined()
    click(development!)
    await flush()

    expect(vi.mocked(put)).toHaveBeenCalledWith('/update/channel', { channel: 'development' })

    // The trade, in plain words: what you gain, what it costs, and what does NOT change.
    expect(container.textContent).toContain('not finished software')
    expect(container.textContent).toContain('not been through release testing')
    expect(container.textContent).toContain("airline's saved data")
    expect(container.textContent).toContain('checks every download against the checksum')
    unmount()
  })

  it('switching back to stable is offered, and posts the stable channel', async () => {
    vi.mocked(put).mockResolvedValue(status({ channel: 'stable' }))
    const { container, unmount } = await render(status({ channel: 'development' }))

    const stable = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Stable')
    click(stable!)
    await flush()

    expect(vi.mocked(put)).toHaveBeenCalledWith('/update/channel', { channel: 'stable' })
    unmount()
  })

  it('a build newer than the channel is reported as ahead, never as an available downgrade', async () => {
    const { container, unmount } = await render(
      status({
        currentVersion: '1.1.0-beta.3',
        channelNewestVersion: '1.0.0',
        aheadOfChannel: true,
        updateAvailable: false,
      }),
    )

    expect(container.textContent).toContain('ahead of the stable channel')
    expect(container.textContent).toContain('1.1.0-beta.3')
    expect(container.textContent).toContain('1.0.0')
    expect(container.textContent).toContain('never replaces a newer build with an older one')

    // And the two things that would be wrong to say or offer.
    expect(container.textContent).not.toContain('You are on the latest version.')
    expect(queryButton(container, 'Download and verify')).toBeNull()
    unmount()
  })

  it('says "latest version" only when that is actually true', async () => {
    const { container, unmount } = await render(status())

    expect(container.textContent).toContain('You are on the latest version.')
    expect(container.textContent).not.toContain('ahead of the')
    unmount()
  })

  it('a check that could not reach the releases page reads as "nothing changed", not an error', async () => {
    const { container, unmount } = await render(status({ lastCheckFailed: true }))

    expect(container.textContent).toContain('could not reach the releases page')
    expect(container.textContent).not.toContain('Error')
    unmount()
  })
})

