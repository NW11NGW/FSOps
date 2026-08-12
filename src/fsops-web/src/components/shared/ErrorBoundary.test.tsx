import { describe, expect, it, vi } from 'vitest'

import { ErrorBoundary } from './ErrorBoundary'
import { click, getByRole, mount, queryByRole, text } from '@/test/domHarness'

/** A child that fails on demand, standing in for the real "stale field in one card" case this
 *  boundary was added for. */
function Card({ broken }: { broken: boolean }) {
  if (broken) throw new Error('Cannot read properties of null (reading `cashBalance`)')
  return <p>Cash balance $12,000.00</p>
}

/** React logs every caught render error, and the boundary logs its own. Both are correct
 *  behaviour, and both would otherwise spray a real stack trace through a passing test run. */
function silenceExpectedErrorLogging() {
  vi.spyOn(console, 'error').mockImplementation(() => {})
}

describe('ErrorBoundary', () => {
  it('renders its children untouched when nothing throws', async () => {
    const { container, unmount } = await mount(
      <ErrorBoundary>
        <Card broken={false} />
      </ErrorBoundary>,
    )

    expect(text(container)).toContain('Cash balance $12,000.00')
    expect(queryByRole(container, 'heading')).toBeNull()

    unmount()
  })

  it('replaces a throwing subtree with a readable explanation rather than a blank page', async () => {
    silenceExpectedErrorLogging()

    const { container, unmount } = await mount(
      <ErrorBoundary>
        <Card broken />
      </ErrorBoundary>,
    )

    // The blank white screen is the failure mode this component exists to prevent, so the
    // meaningful assertion is that something is actually on screen, not merely that no crash
    // escaped.
    expect(text(container)).not.toBe('')
    expect(getByRole(container, 'heading', { name: 'Something went wrong on this screen' })).toBeTruthy()
    expect(text(container)).toContain('The rest of FSOps is still running, and nothing has been lost.')

    unmount()
  })

  it('shows the actual error message, so a bug report can name what failed', async () => {
    silenceExpectedErrorLogging()

    const { container, unmount } = await mount(
      <ErrorBoundary>
        <Card broken />
      </ErrorBoundary>,
    )

    expect(text(container)).toContain('Cannot read properties of null (reading `cashBalance`)')

    unmount()
  })

  it('"Try again" really re-renders the children, rather than only clearing the message', async () => {
    silenceExpectedErrorLogging()

    const mounted = await mount(
      <ErrorBoundary>
        <Card broken />
      </ErrorBoundary>,
    )
    expect(text(mounted.container)).toContain('Something went wrong on this screen')

    // The underlying cause goes away (a refetch succeeded, a heartbeat delivered a complete
    // payload) and only then does the user retry - which is the sequence that has to work.
    await mounted.rerender(
      <ErrorBoundary>
        <Card broken={false} />
      </ErrorBoundary>,
    )
    expect(text(mounted.container)).toContain('Something went wrong on this screen')

    click(getByRole(mounted.container, 'button', { name: 'Try again' }))

    expect(text(mounted.container)).toContain('Cash balance $12,000.00')
    expect(text(mounted.container)).not.toContain('Something went wrong on this screen')

    mounted.unmount()
  })
})
