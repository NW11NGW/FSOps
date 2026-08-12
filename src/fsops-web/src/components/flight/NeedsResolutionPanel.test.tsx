import { describe, expect, it, vi } from 'vitest'

import { NeedsResolutionPanel } from './NeedsResolutionPanel'
import { click, findButton, flush, mount, text } from '@/test/domHarness'

/** A promise the test controls the resolution of, so it can assert on the in-flight ("busy") state
 *  before letting the async action complete. */
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => {
    resolve = r
  })
  return { promise, resolve }
}

async function render(overrides: {
  onResume?: () => void
  onCompleteWithEstimates?: () => Promise<void>
  onAbandon?: () => Promise<void>
} = {}) {
  const onResume = overrides.onResume ?? vi.fn()
  const onCompleteWithEstimates = overrides.onCompleteWithEstimates ?? vi.fn(async () => {})
  const onAbandon = overrides.onAbandon ?? vi.fn(async () => {})
  const mounted = await mount(
    <NeedsResolutionPanel onResume={onResume} onCompleteWithEstimates={onCompleteWithEstimates} onAbandon={onAbandon} />,
  )
  return { ...mounted, onResume, onCompleteWithEstimates, onAbandon }
}

describe('NeedsResolutionPanel', () => {
  it('explains why the flight needs attention and offers the three ways to resolve it', async () => {
    const { container, unmount } = await render()
    const body = text(container)
    expect(body).toContain('This flight needs your attention')
    expect(body).toContain('Resume tracking')
    expect(body).toContain('Complete with estimates')
    expect(body).toContain('Abandon')
    unmount()
  })

  it('calls onResume when "Check again" is clicked', async () => {
    const { container, onResume, unmount } = await render()

    click(findButton(container, 'Check again'))

    expect(onResume).toHaveBeenCalledTimes(1)
    unmount()
  })

  it('shows "Completing…" and disables the other actions while completion is in flight, then recovers', async () => {
    const gate = deferred<void>()
    const { container, unmount } = await render({ onCompleteWithEstimates: () => gate.promise })

    click(findButton(container, 'Complete with estimates'))

    expect(text(container)).toContain('Completing…')
    // Busy disables every action, not just the one clicked - the other two must not be actionable
    // mid-completion.
    expect(findButton(container, 'Check again').disabled).toBe(true)
    expect(findButton(container, 'Abandon flight').disabled).toBe(true)

    gate.resolve()
    await flush()

    expect(text(container)).toContain('Complete with estimates')
    expect(text(container)).not.toContain('Completing…')
    expect(findButton(container, 'Check again').disabled).toBe(false)

    unmount()
  })

  it('shows "Abandoning…" and disables the other actions while abandoning is in flight, then recovers', async () => {
    const gate = deferred<void>()
    const { container, unmount } = await render({ onAbandon: () => gate.promise })

    click(findButton(container, 'Abandon flight'))

    expect(text(container)).toContain('Abandoning…')
    expect(findButton(container, 'Check again').disabled).toBe(true)
    expect(findButton(container, 'Complete with estimates').disabled).toBe(true)

    gate.resolve()
    await flush()

    expect(text(container)).toContain('Abandon flight')
    expect(text(container)).not.toContain('Abandoning…')
    expect(findButton(container, 'Check again').disabled).toBe(false)

    unmount()
  })

  it('calls onAbandon exactly once per click, with no accidental double-submit while busy', async () => {
    const onAbandon = vi.fn(async () => {})
    const { container, unmount } = await render({ onAbandon })

    const button = findButton(container, 'Abandon flight')
    click(button)
    // A second click while disabled must not be possible to turn into a second call - the button
    // is disabled the instant the first click's handler sets busy state.
    click(button)
    await flush()

    expect(onAbandon).toHaveBeenCalledTimes(1)
    unmount()
  })
})
