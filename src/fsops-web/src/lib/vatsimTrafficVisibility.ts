const STORAGE_KEY = 'fsops-liveops-traffic-visible'

/**
 * Whether the dashboard's live map should draw other VATSIM pilots' traffic (G11). **Off by
 * default** - including for a brand-new user with nothing stored yet - because the map should be
 * clean on first load, not opt-out. Only an explicit `true` written by `writeVatsimTrafficVisible`
 * turns it on; a missing key, a malformed value, or `localStorage` throwing (locked-down embeds
 * such as the in-sim panel webview) must all resolve to hidden, never to a quiet default-on.
 *
 * Persistence follows the same convention as the live map's other two toggles
 * (`fsops-liveops-empty-collapsed`, `fsops-liveops-legend-collapsed`): a `fsops-*` key, read once
 * on mount via `useState(readVatsimTrafficVisible)`, written on every change via an effect.
 */
export function readVatsimTrafficVisible(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

export function writeVatsimTrafficVisible(value: boolean): void {
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
