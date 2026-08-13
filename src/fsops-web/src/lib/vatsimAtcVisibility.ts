const STORAGE_KEY = 'fsops-liveops-atc-visible'

/**
 * Whether the dashboard should show VATSIM ATC - the map's sector/terminal coverage layer and the
 * "ATC coverage" card that lists the same controllers. **Off by default**, including for a
 * brand-new user with nothing stored yet, exactly like its sibling
 * `vatsimTrafficVisibility`: the map should be clean on first load, not opt-out. Only an explicit
 * `true` written by `writeVatsimAtcVisible` turns it on; a missing key, a malformed value, or
 * `localStorage` throwing (a locked-down embed or a privacy-restricted browser) must all resolve
 * to hidden, never to a quiet default-on.
 *
 * Persistence follows the same convention as the live map's other toggles
 * (`fsops-liveops-traffic-visible`, `fsops-liveops-empty-collapsed`,
 * `fsops-liveops-legend-collapsed`): a `fsops-*` key, read once on mount via
 * `useState(readVatsimAtcVisible)`, written on every change via an effect.
 */
export function readVatsimAtcVisible(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

export function writeVatsimAtcVisible(value: boolean): void {
  try {
    if (typeof window === 'undefined') return
    if (value) {
      window.localStorage.setItem(STORAGE_KEY, 'true')
    } else {
      window.localStorage.removeItem(STORAGE_KEY)
    }
  } catch {
    // Best-effort only - never let a locked-down storage break the dashboard.
  }
}
