import { beforeEach, describe, expect, it, vi } from 'vitest'

import { MsfsPanelSection } from './MsfsPanelSection'
import { SettingsProvider } from '@/hooks/useSettings'
import { click, findButton, flush, mount, typeInto } from '@/test/domHarness'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { del, get, post, put } from '@/lib/api'

const SAVED = 'C:\\MSFS\\Packages\\Community'
const OTHER = 'D:\\MSFS\\Packages\\Community'

interface StatusOverrides {
  success?: boolean
  reason?: string | null
  installed?: boolean
  installedPath?: string | null
  installedVersion?: string | null
  expectedVersion?: string
  spbPresent?: boolean
  toolbarWillAppearInSim?: boolean
  message?: string
  installedPort?: string | null
  expectedPort?: string | null
  missingFiles?: string[]
}

function status(overrides: StatusOverrides = {}) {
  return {
    success: true,
    reason: null,
    installed: true,
    installedPath: `${SAVED}\\fsops-panel`,
    installedVersion: '1.0.0',
    expectedVersion: '1.0.0',
    spbPresent: true,
    toolbarWillAppearInSim: true,
    filesWritten: 0,
    message: 'Installed and up to date.',
    installedPort: '5977',
    expectedPort: '5977',
    missingFiles: [] as string[],
    ...overrides,
  }
}

/**
 * A package that IS in the folder but has lost files - what the server reports after the player (or
 * an add-on manager) deletes part of it. `installed` is false because it now means "complete", so
 * every assertion about this state is really checking that the UI reads the pair of fields rather
 * than the flag alone.
 */
function damaged(missingFiles: string[], overrides: StatusOverrides = {}) {
  return status({ installed: false, missingFiles, ...overrides })
}

/**
 * Mocks the two GETs SettingsProvider makes plus the panel's own status/detect calls. `communityFolderPath`
 * is what the section treats as "currently configured", which drives nearly every state it can be in.
 */
function mockApi({
  communityFolderPath = SAVED as string | null,
  panelStatus = status(),
  statusFails = false,
  candidates = [] as { path: string; source: string; exists: boolean }[],
} = {}) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return {
        currencyCode: 'USD', distanceUnit: 'Nm', altitudeUnit: 'Feet', weightUnit: 'Kg',
        timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', communityFolderPath, simBriefPilotId: null,
      } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return [] as unknown as ReturnType<typeof get>
    if (path === '/panel/status') {
      if (statusFails) throw new Error('offline')
      return panelStatus as unknown as ReturnType<typeof get>
    }
    if (path === '/panel/detect') return candidates as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })

  vi.mocked(post).mockImplementation(async (path: string) => {
    if (path === '/panel/validate') {
      return { valid: true, reason: null, resolvedPath: OTHER } as unknown as ReturnType<typeof post>
    }
    throw new Error(`unexpected POST ${path}`)
  })

  vi.mocked(put).mockResolvedValue({
    currencyCode: 'USD', distanceUnit: 'Nm', altitudeUnit: 'Feet', weightUnit: 'Kg',
    timeDisplay: 'Utc', use24HourClock: true, theme: 'dark', communityFolderPath: OTHER, simBriefPilotId: null,
  })
}

function render() {
  return mount(
    <SettingsProvider>
      <MsfsPanelSection />
    </SettingsProvider>,
  )
}

beforeEach(() => {
  vi.mocked(get).mockReset()
  vi.mocked(post).mockReset()
  vi.mocked(put).mockReset()
  vi.mocked(del).mockReset()
})

describe('MsfsPanelSection - reading real status', () => {
  it('reports what is on disk, not what onboarding once did', async () => {
    mockApi()
    const mounted = await render()
    await flush()

    expect(get).toHaveBeenCalledWith('/panel/status', undefined)
    expect(mounted.container.textContent).toContain('Installed')
    expect(mounted.container.textContent).toContain('up to date')

    mounted.unmount()
  })

  it('says the toolbar will appear when the compiled component is on disk', async () => {
    mockApi({ panelStatus: status({ spbPresent: true, toolbarWillAppearInSim: true }) })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('Appears in the MSFS toolbar')

    mounted.unmount()
  })

  it('is honest rather than reassuring if the compiled component is ever missing', async () => {
    // The .spb ships today, so this is the "something went wrong with the install" case - it must
    // still say the button will not appear rather than quietly reporting a healthy install.
    mockApi({ panelStatus: status({ spbPresent: false, toolbarWillAppearInSim: false }) })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('Toolbar button')
    expect(mounted.container.textContent).toContain('doesn\u2019t include the compiled panel component')
    expect(mounted.container.textContent).not.toContain('Appears in the MSFS toolbar')

    mounted.unmount()
  })

  it('spots a panel still calling the port FSOps has since moved off', async () => {
    // The files are all present and correct here - nothing else in the app would notice that the
    // panel is talking to a port nobody is listening on, and in the sim it just shows nothing.
    mockApi({ panelStatus: status({ installedPort: '5977', expectedPort: '5978' }) })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('Needs repair')
    expect(mounted.container.textContent).toContain('Port 5977, but FSOps is on 5978')
    expect(findButton(mounted.container, 'Reinstall / repair')).toBeTruthy()

    mounted.unmount()
  })

  it('does not invent port drift when the config could not be read', async () => {
    mockApi({ panelStatus: status({ installedPort: null }) })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).not.toContain('but FSOps is on')
    expect(mounted.container.textContent).toContain('Installed')

    mounted.unmount()
  })

  it('surfaces version drift with the two versions, rather than just claiming "installed"', async () => {
    mockApi({ panelStatus: status({ installedVersion: '0.9.0', expectedVersion: '1.0.0' }) })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('Update available')
    expect(mounted.container.textContent).toContain('0.9.0 installed, FSOps expects 1.0.0')

    mounted.unmount()
  })

  it('shows a refusal reason when the configured folder is no longer usable', async () => {
    mockApi({
      panelStatus: status({ success: false, installed: false, reason: 'That\u2019s a drive root, not a Community folder.' }),
    })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('not a Community folder')
    expect(mounted.container.textContent).toContain('Needs attention')

    mounted.unmount()
  })

  it('offers a retry rather than a blank card when the status call fails', async () => {
    mockApi({ statusFails: true })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('Could not read the panel status')
    expect(findButton(mounted.container, 'Try again')).toBeTruthy()

    mounted.unmount()
  })
})

describe('MsfsPanelSection - no folder configured', () => {
  it('explains nothing is installed and offers what detection found', async () => {
    mockApi({
      communityFolderPath: null,
      panelStatus: status({ installed: false, installedPath: null, installedVersion: null, message: 'No Community folder configured yet.' }),
      candidates: [{ path: SAVED, source: 'UserCfg.opt (Steam / non-Store install)', exists: true }],
    })
    const mounted = await render()
    await flush()

    expect(mounted.container.textContent).toContain('No Community folder is set')
    expect(mounted.container.textContent).toContain('Found on this PC')
    expect(mounted.container.textContent).toContain(SAVED)

    // Picking a detected folder fills the field so it can be saved - a player who skipped the
    // panel at onboarding has a way in.
    const candidate = mounted.container.querySelector('button[aria-pressed]')
    click(candidate!)
    await flush()
    expect((mounted.container.querySelector('#community-folder') as HTMLInputElement).value).toBe(SAVED)

    mounted.unmount()
  })
})

describe('MsfsPanelSection - changing the folder', () => {
  async function startFolderChange() {
    mockApi()
    const mounted = await render()
    await flush()

    typeInto(mounted.container.querySelector('#community-folder') as HTMLInputElement, OTHER)
    await flush()
    click(findButton(mounted.container, 'Save folder'))
    await flush()

    return mounted
  }

  it('never silently orphans the old install - it asks first', async () => {
    const mounted = await startFolderChange()

    expect(document.body.textContent).toContain('Move the panel to the new folder?')
    expect(put).not.toHaveBeenCalled()

    mounted.unmount()
  })

  it('"Move the panel" installs into the new folder and clears the old one before saving the path', async () => {
    const mounted = await startFolderChange()

    vi.mocked(post).mockImplementation(async (path: string) => {
      if (path === '/panel/validate') return { valid: true, reason: null, resolvedPath: OTHER } as unknown as ReturnType<typeof post>
      if (path === '/panel/move') {
        return {
          success: true,
          reason: null,
          message: 'Panel installed.',
          install: status({ installedPath: `${OTHER}\\fsops-panel`, message: 'Panel installed.' }),
          oldCopyRemoved: true,
          oldCopyMessage: `The old copy in ${SAVED}\\fsops-panel was removed.`,
        } as unknown as ReturnType<typeof post>
      }
      throw new Error(`unexpected POST ${path}`)
    })

    click(findButton(document.body, 'Move the panel'))
    await flush()

    expect(post).toHaveBeenCalledWith('/panel/move', { fromPath: SAVED, toPath: OTHER })
    expect(put).toHaveBeenCalledWith('/settings', expect.objectContaining({ communityFolderPath: OTHER }))

    mounted.unmount()
  })

  it('"Just change the folder" saves the path and leaves the old copy alone', async () => {
    const mounted = await startFolderChange()

    click(findButton(document.body, 'Just change the folder'))
    await flush()

    expect(put).toHaveBeenCalledWith('/settings', expect.objectContaining({ communityFolderPath: OTHER }))
    expect(post).not.toHaveBeenCalledWith('/panel/move', expect.anything())

    mounted.unmount()
  })

  it('clearing the folder offers to remove the panel first rather than abandoning it', async () => {
    mockApi()
    const mounted = await render()
    await flush()

    typeInto(mounted.container.querySelector('#community-folder') as HTMLInputElement, '')
    await flush()
    click(findButton(mounted.container, 'Save folder'))
    await flush()

    expect(document.body.textContent).toContain('Clear your Community folder?')

    vi.mocked(del).mockResolvedValue(status({ installed: false, message: 'Panel removed from your Community folder.' }))
    click(findButton(document.body, 'Remove the panel and clear'))
    await flush()

    expect(del).toHaveBeenCalledWith('/panel/uninstall', { path: SAVED })
    expect(put).toHaveBeenCalledWith('/settings', expect.objectContaining({ communityFolderPath: null }))

    mounted.unmount()
  })

  it('saves straight away when there is nothing installed to worry about', async () => {
    mockApi({ panelStatus: status({ installed: false, installedPath: null, installedVersion: null, message: 'Not installed yet.' }) })
    const mounted = await render()
    await flush()

    typeInto(mounted.container.querySelector('#community-folder') as HTMLInputElement, OTHER)
    await flush()
    click(findButton(mounted.container, 'Save folder'))
    await flush()

    expect(document.body.textContent).not.toContain('Move the panel to the new folder?')
    expect(put).toHaveBeenCalledWith('/settings', expect.objectContaining({ communityFolderPath: OTHER }))

    mounted.unmount()
  })
})

describe('MsfsPanelSection - install, repair and remove', () => {
  it('offers an install to someone who set a folder but skipped the panel', async () => {
    mockApi({ panelStatus: status({ installed: false, installedPath: null, installedVersion: null, message: 'Not installed yet.' }) })
    const mounted = await render()
    await flush()

    vi.mocked(post).mockResolvedValue(status({ message: 'Panel installed.' }))
    click(findButton(mounted.container, 'Install panel'))
    await flush()

    expect(post).toHaveBeenCalledWith('/panel/install', { path: SAVED })

    mounted.unmount()
  })

  it('repairs in place against the configured folder', async () => {
    mockApi()
    const mounted = await render()
    await flush()

    vi.mocked(post).mockResolvedValue(status({ message: 'Panel installed.' }))
    click(findButton(mounted.container, 'Reinstall / repair'))
    await flush()

    expect(post).toHaveBeenCalledWith('/panel/install', { path: SAVED })

    mounted.unmount()
  })

  it('confirms before removing, and only then deletes', async () => {
    mockApi()
    const mounted = await render()
    await flush()

    click(findButton(mounted.container, 'Remove panel'))
    await flush()

    expect(document.body.textContent).toContain('Remove the panel from MSFS?')
    expect(del).not.toHaveBeenCalled()

    vi.mocked(del).mockResolvedValue(status({ installed: false, message: 'Panel removed from your Community folder.' }))
    click(findButton(document.body, 'Remove the panel'))
    await flush()

    expect(del).toHaveBeenCalledWith('/panel/uninstall', { path: SAVED })

    mounted.unmount()
  })
})

/**
 * A package that is present but has lost files. This shipped reporting itself as perfectly healthy:
 * "installed" was decided from manifest.json alone, so deleting the panel's own FSOpsPanel.js left a
 * green "Installed - up to date" over a panel that renders nothing in the sim, with nothing anywhere
 * suggesting the repair that would fix it.
 */
describe('MsfsPanelSection - a damaged install', () => {
  const MISSING_JS = 'html_ui/InGamePanels/FSOpsPanel/FSOpsPanel.js'

  it('calls it out instead of reporting a healthy install', async () => {
    mockApi({ panelStatus: damaged([MISSING_JS]) })
    const mounted = await render()
    await flush()

    const rendered = mounted.container.textContent ?? ''
    expect(rendered).toContain('Needs repair')
    expect(rendered).toContain('1 file is missing')
    expect(rendered).toContain(MISSING_JS)
    expect(rendered).not.toContain('Not installed in this folder')

    mounted.unmount()
  })

  it('offers repair and removal, not a fresh install', async () => {
    // Tying these to the healthy state would hide the fix behind the very damage it repairs.
    mockApi({ panelStatus: damaged([MISSING_JS]) })
    const mounted = await render()
    await flush()

    expect(findButton(mounted.container, 'Reinstall / repair')).toBeTruthy()
    expect(findButton(mounted.container, 'Remove panel')).toBeTruthy()
    expect(
      Array.from(mounted.container.querySelectorAll('button')).some((b) => b.textContent === 'Install panel'),
    ).toBe(false)

    mounted.unmount()
  })

  it('still reports port drift, rather than letting one fault mask the other', async () => {
    // Regression: once `installed` came to mean "complete", the drift check keyed off it and a
    // damaged install confidently claimed its port "matches this copy of FSOps" - about the wrong
    // port. A confident wrong answer is worse than silence.
    mockApi({ panelStatus: damaged([MISSING_JS], { installedPort: '5977', expectedPort: '5978' }) })
    const mounted = await render()
    await flush()

    const rendered = mounted.container.textContent ?? ''
    expect(rendered).toContain('but FSOps is on 5978')
    expect(rendered).not.toContain('matches this copy of FSOps')

    mounted.unmount()
  })

  it('blames the copy, not the build, when the compiled component is the missing file', async () => {
    // "A future update will finish it" sends the player away to wait for something that will never
    // arrive. The template ships a .spb, so a missing one is theirs to repair right now.
    mockApi({
      panelStatus: damaged(['InGamePanels/FSOpsPanel.spb'], { spbPresent: false, toolbarWillAppearInSim: false }),
    })
    const mounted = await render()
    await flush()

    const rendered = mounted.container.textContent ?? ''
    expect(rendered).toContain('missing from your copy')
    expect(rendered).not.toContain('a future update will finish it')
    // The honest-reporting property survives: a missing component still means no button.
    expect(rendered).not.toContain('Appears in the MSFS toolbar')

    mounted.unmount()
  })

  it('still blames the build when this build genuinely ships no compiled component', async () => {
    // Nothing is missing relative to the template - the build simply has none to give.
    mockApi({ panelStatus: status({ spbPresent: false, toolbarWillAppearInSim: false, missingFiles: [] }) })
    const mounted = await render()
    await flush()

    const rendered = mounted.container.textContent ?? ''
    expect(rendered).toContain('doesn’t include the compiled panel component')
    expect(rendered).not.toContain('missing from your copy')

    mounted.unmount()
  })
})
