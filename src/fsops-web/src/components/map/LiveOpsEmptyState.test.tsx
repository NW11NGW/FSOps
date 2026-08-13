import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'

import { LiveOpsEmptyState } from './LiveOpsEmptyState'
import { click, flush, getByRole, mount, queryByRole, text } from '@/test/domHarness'

const STORAGE_KEY = 'fsops-liveops-empty-collapsed'

beforeEach(() => {
  window.localStorage.clear()
})

afterEach(() => {
  window.localStorage.clear()
})

async function render(hasAircraft: boolean) {
  return mount(
    <MemoryRouter>
      <LiveOpsEmptyState hasAircraft={hasAircraft} />
    </MemoryRouter>,
  )
}

describe('LiveOpsEmptyState - nothing airborne', () => {
  it('shows the full message with a dismiss control when nothing is airborne', async () => {
    const { container, unmount } = await render(false)
    expect(text(container)).toContain('Nothing airborne right now')
    expect(getByRole(container, 'button', { name: 'Hide the live status message' })).toBeTruthy()
    unmount()
  })

  it('renders nothing at all once something is airborne', async () => {
    const { container, unmount } = await render(true)
    expect(text(container)).toBe('')
    expect(container.querySelector('div')).toBeNull()
    unmount()
  })
})

describe('LiveOpsEmptyState - dismiss collapses rather than removing the message entirely', () => {
  it('collapses to a small reopenable pill on dismiss, and the map area keeps click-through', async () => {
    const { container, unmount } = await render(false)

    click(getByRole(container, 'button', { name: 'Hide the live status message' }))
    await flush()

    expect(queryByRole(container, 'button', { name: 'Hide the live status message' })).toBeNull()
    const pill = getByRole(container, 'button', { name: 'Show the live status message: nothing airborne right now' })
    expect(pill).toBeTruthy()
    expect(text(container)).toContain('Nothing airborne')

    // Reopen from the pill.
    click(pill)
    await flush()
    expect(getByRole(container, 'button', { name: 'Hide the live status message' })).toBeTruthy()
    unmount()
  })

  it('persists the collapsed choice in localStorage so it does not nag on the next mount', async () => {
    const { container, unmount } = await render(false)
    click(getByRole(container, 'button', { name: 'Hide the live status message' }))
    await flush()
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')
    unmount()

    const second = await render(false)
    expect(queryByRole(second.container, 'button', { name: 'Hide the live status message' })).toBeNull()
    expect(getByRole(second.container, 'button', { name: /Show the live status message/ })).toBeTruthy()
    second.unmount()
  })

  it('collapses on Escape the same way the close button does', async () => {
    const { container, unmount } = await render(false)
    act(() => {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    })
    await flush()
    expect(queryByRole(container, 'button', { name: 'Hide the live status message' })).toBeNull()
    expect(getByRole(container, 'button', { name: /Show the live status message/ })).toBeTruthy()
    unmount()
  })

  it('re-arms once a real flight starts, so dismissing once does not hide the message forever', async () => {
    const { container, rerender, unmount } = await render(false)
    click(getByRole(container, 'button', { name: 'Hide the live status message' }))
    await flush()
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')

    // A flight starts.
    await rerender(
      <MemoryRouter>
        <LiveOpsEmptyState hasAircraft={true} />
      </MemoryRouter>,
    )
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()

    // Flight ends - nothing airborne again - the full message is back, not the collapsed pill.
    await rerender(
      <MemoryRouter>
        <LiveOpsEmptyState hasAircraft={false} />
      </MemoryRouter>,
    )
    expect(getByRole(container, 'button', { name: 'Hide the live status message' })).toBeTruthy()
    unmount()
  })
})
