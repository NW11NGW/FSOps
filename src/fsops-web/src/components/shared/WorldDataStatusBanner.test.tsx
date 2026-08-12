import { describe, expect, it } from 'vitest'

import { WorldDataStatusBanner } from './WorldDataStatusBanner'
import { mount, text } from '@/test/domHarness'
import type { WorldDataStatus } from '@/types/airport'

function status(overrides: Partial<WorldDataStatus> = {}): WorldDataStatus {
  return {
    seeded: true,
    airportCount: 78000,
    runwayCount: 42000,
    importInProgress: false,
    progressPercent: 100,
    ...overrides,
  }
}

describe('WorldDataStatusBanner - staying out of the way', () => {
  it('renders nothing once the data is seeded and no import is running', async () => {
    const { container, unmount } = await mount(<WorldDataStatusBanner status="ready" data={status()} />)

    // This banner sits above every page. Rendering anything at all in the steady state would be a
    // permanent strip of chrome for a one-off event.
    expect(text(container)).toBe('')

    unmount()
  })

  it('renders nothing while the status itself is still loading', async () => {
    const { container, unmount } = await mount(<WorldDataStatusBanner status="loading" data={null} />)

    // Flashing "not imported yet" for a moment on every load, before the status arrives, would be
    // alarming and wrong.
    expect(text(container)).toBe('')

    unmount()
  })

  it('renders nothing when the status could not be read at all', async () => {
    const { container, unmount } = await mount(<WorldDataStatusBanner status="error" data={null} />)

    expect(text(container)).toBe('')

    unmount()
  })
})

describe('WorldDataStatusBanner - an import in progress', () => {
  it('says what is happening, how far it has got, and that it happens only once', async () => {
    const { container, unmount } = await mount(
      <WorldDataStatusBanner
        status="ready"
        data={status({ seeded: false, importInProgress: true, airportCount: 31500, progressPercent: 40.4 })}
      />,
    )

    const body = text(container)
    expect(body).toContain('Importing world airport data')
    expect(body).toContain('31,500 airports imported so far')
    expect(body).toContain('this only happens once')
    expect(body).toContain('40%')

    unmount()
  })

  it('clamps a nonsense progress figure into 0-100 rather than rendering it', async () => {
    const over = await mount(
      <WorldDataStatusBanner status="ready" data={status({ seeded: false, importInProgress: true, progressPercent: 143 })} />,
    )
    expect(text(over.container)).toContain('100%')
    expect(text(over.container)).not.toContain('143%')
    over.unmount()

    const under = await mount(
      <WorldDataStatusBanner status="ready" data={status({ seeded: false, importInProgress: true, progressPercent: -5 })} />,
    )
    expect(text(under.container)).toContain('0%')
    expect(text(under.container)).not.toContain('-5%')
    under.unmount()
  })

  it('shows the import banner, not the warning, even while the database is still unseeded', async () => {
    const { container, unmount } = await mount(
      <WorldDataStatusBanner status="ready" data={status({ seeded: false, importInProgress: true })} />,
    )

    // Mid-import the data legitimately is not seeded yet. Warning that route planning is
    // unavailable, while the fix is visibly already running, would send the player looking for a
    // problem that is in the middle of solving itself.
    expect(text(container)).not.toContain('Airport search and route planning will be unavailable')

    unmount()
  })
})

describe('WorldDataStatusBanner - nothing imported and nothing running', () => {
  it('warns, and names what will not work until the import runs', async () => {
    const { container, unmount } = await mount(
      <WorldDataStatusBanner status="ready" data={status({ seeded: false, importInProgress: false, airportCount: 0 })} />,
    )

    const body = text(container)
    expect(body).toContain("World airport data hasn't been imported yet")
    expect(body).toContain('Airport search and route planning will be unavailable')

    unmount()
  })
})
