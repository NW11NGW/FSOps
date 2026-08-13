import { createElement } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import App from './App'
import { SettingsProvider } from '@/hooks/useSettings'
import { flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

const mockedGet = vi.mocked(get)

/**
 * GET /airline answers 204 - parsed as `undefined` - when no airline exists, which is what puts the
 * gate into its wizard state. Settings are served because the wizard renders inside a real
 * SettingsProvider; anything else the tree asks for on the way is irrelevant here and is left
 * pending rather than answered with a fake shape that could drift from the real one.
 */
function noAirlineYet() {
  mockedGet.mockImplementation((path: string) => {
    if (path === '/airline') return Promise.resolve(undefined as never)
    const settings = settingsResponseFor(path)
    if (settings) return Promise.resolve(settings as never)
    return new Promise(() => {}) as never
  })
}

/**
 * Mounts App at `path` and lets the lazy route chunk and its effects settle.
 *
 * The wizard is behind `React.lazy`, and under vitest a dynamic import resolves only once the
 * module has actually been transformed - which is real I/O, not a microtask, so flushing alone can
 * leave the tree parked on its Suspense fallback forever. Importing the module first puts it in the
 * module cache, so `lazy` resolves immediately and the test asserts on the rendered branch rather
 * than on a spinner.
 */
async function renderAt(path: string) {
  await import('@/components/onboarding/OnboardingWizard')
  const mounted = await mount(
    createElement(
      SettingsProvider,
      null,
      createElement(MemoryRouter, { initialEntries: [path] }, createElement(App)),
    ),
  )
  for (let i = 0; i < 10; i++) await flush()
  return mounted
}

describe('App routing when no airline exists', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    noAirlineYet()
  })

  it('shows the onboarding wizard on a normal route', async () => {
    const { container, unmount } = await renderAt('/')
    expect(text(container)).toContain('Let’s found your airline')
    unmount()
  })

  /** Any route, not just "/", is the wizard until an airline exists - there is no way past it. */
  it('shows the onboarding wizard on a deep route too', async () => {
    const { container, unmount } = await renderAt('/fleet')
    expect(text(container)).toContain('Let’s found your airline')
    unmount()
  })
})
