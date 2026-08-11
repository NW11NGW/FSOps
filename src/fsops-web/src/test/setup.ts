/**
 * Vitest setup for the handful of dialog tests that mount real components via `react-dom` (see
 * domHarness.ts). React 18's `act()` only batches/flushes synchronously when it recognises the
 * host environment as a test environment - without this flag, effects and state updates from
 * `act()` calls silently fail to flush, which reads as "nothing happened" rather than an error.
 */
import { afterEach } from 'vitest'

declare global {
  // eslint-disable-next-line no-var
  var IS_REACT_ACT_ENVIRONMENT: boolean
}

globalThis.IS_REACT_ACT_ENVIRONMENT = true

/**
 * Radix's Dialog content portals directly into `document.body`, outside the mount container
 * returned by domHarness's `mount()`. If a test throws mid-assertion, its own `unmount()` call
 * never runs, and that portal content is left behind for the next test to accidentally query
 * against - a stale '#some-input' from a FAILED earlier test would otherwise silently satisfy a
 * later test's selector. Clearing the body after every test removes that cross-test coupling
 * regardless of how a test exits.
 */
afterEach(() => {
  document.body.innerHTML = ''
})
