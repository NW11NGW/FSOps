import { beforeEach, describe, expect, it, vi } from 'vitest'

import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, queryByRole, typeInto } from '@/test/domHarness'
import { TEST_CURRENCIES, TEST_SETTINGS } from '@/test/settingsStub'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'
import { OnlinePresenceStep } from './OnlinePresenceStep'
import { DEFAULT_WIZARD_DATA, type WizardData } from '../wizardData'

function mockSettings(overrides: { simBriefPilotId?: string | null; vatsimCid?: string | null } = {}) {
  vi.mocked(get).mockImplementation(async (path: string) => {
    if (path === '/settings') {
      return { ...TEST_SETTINGS, ...overrides } as unknown as ReturnType<typeof get>
    }
    if (path === '/settings/currencies') return TEST_CURRENCIES as unknown as ReturnType<typeof get>
    throw new Error(`unexpected GET ${path}`)
  })
}

async function render(data: WizardData, onChange = vi.fn()) {
  const mounted = await mount(
    <SettingsProvider>
      <OnlinePresenceStep data={data} onChange={onChange} />
    </SettingsProvider>,
  )
  await flush()
  return { mounted, onChange }
}

beforeEach(() => {
  vi.mocked(get).mockReset()
})

describe('OnlinePresenceStep - a fresh install (nothing set)', () => {
  it('shows two editable, empty fields with what each unlocks', async () => {
    mockSettings()
    const { mounted } = await render(DEFAULT_WIZARD_DATA)

    expect(mounted.container.textContent).toContain('SimBrief Pilot ID')
    expect(mounted.container.textContent).toContain('Fly screen')
    expect(mounted.container.textContent).toContain('VATSIM CID')
    expect(mounted.container.textContent).toContain('flown online')

    const simbrief = queryByRole(mounted.container, 'textbox', { name: /SimBrief Pilot ID/ }) as HTMLInputElement
    const vatsim = queryByRole(mounted.container, 'textbox', { name: /VATSIM CID/ }) as HTMLInputElement
    expect(simbrief).toBeTruthy()
    expect(vatsim).toBeTruthy()
    expect(simbrief.value).toBe('')
    expect(vatsim.value).toBe('')

    mounted.unmount()
  })

  it('is fully skippable - leaving both blank shows no format warning', async () => {
    mockSettings()
    const { mounted } = await render(DEFAULT_WIZARD_DATA)

    expect(mounted.container.textContent).toContain('Leave blank to skip')
    expect(mounted.container.querySelector('.text-warning')).toBeNull()

    mounted.unmount()
  })

  it('typing a valid value calls onChange with it', async () => {
    mockSettings()
    const { mounted, onChange } = await render(DEFAULT_WIZARD_DATA)

    const simbrief = queryByRole(mounted.container, 'textbox', { name: /SimBrief Pilot ID/ }) as HTMLInputElement
    typeInto(simbrief, '123456')
    expect(onChange).toHaveBeenCalledWith({ simBriefPilotId: '123456' })

    mounted.unmount()
  })

  it('a non-numeric value only hints at the expected shape - it never blocks or refuses the input', async () => {
    mockSettings()
    const data: WizardData = { ...DEFAULT_WIZARD_DATA, vatsimCid: 'abc123' }
    const { mounted } = await render(data)

    const vatsim = queryByRole(mounted.container, 'textbox', { name: /VATSIM CID/ }) as HTMLInputElement
    expect(vatsim.value).toBe('abc123')
    expect(mounted.container.textContent).toContain('Numbers only')

    mounted.unmount()
  })

  it('clearing a field sends null, not an empty string', async () => {
    mockSettings()
    const data: WizardData = { ...DEFAULT_WIZARD_DATA, vatsimCid: '1234567' }
    const { mounted, onChange } = await render(data)

    const vatsim = queryByRole(mounted.container, 'textbox', { name: /VATSIM CID/ }) as HTMLInputElement
    typeInto(vatsim, '')
    expect(onChange).toHaveBeenCalledWith({ vatsimCid: null })

    mounted.unmount()
  })
})

describe('OnlinePresenceStep - an existing value on file', () => {
  it('shows an already-set field locked, never re-prompting for it, while the other field stays editable', async () => {
    mockSettings({ simBriefPilotId: '999999' })
    const data: WizardData = { ...DEFAULT_WIZARD_DATA, simBriefPilotId: '999999' }
    const { mounted, onChange } = await render(data)

    expect(mounted.container.textContent).toContain('999999')
    expect(mounted.container.textContent).toContain('Already set')
    expect(queryByRole(mounted.container, 'textbox', { name: /SimBrief Pilot ID/ })).toBeNull()

    const vatsim = queryByRole(mounted.container, 'textbox', { name: /VATSIM CID/ }) as HTMLInputElement
    expect(vatsim).toBeTruthy()
    typeInto(vatsim, '1234567')
    expect(onChange).toHaveBeenCalledWith({ vatsimCid: '1234567' })

    mounted.unmount()
  })

  it('locks both fields when both already have a value - no input exists to accidentally clear either', async () => {
    mockSettings({ simBriefPilotId: '111111', vatsimCid: '2222222' })
    const data: WizardData = { ...DEFAULT_WIZARD_DATA, simBriefPilotId: '111111', vatsimCid: '2222222' }
    const { mounted } = await render(data)

    expect(mounted.container.querySelectorAll('input').length).toBe(0)
    expect(mounted.container.textContent).toContain('111111')
    expect(mounted.container.textContent).toContain('2222222')

    mounted.unmount()
  })
})
