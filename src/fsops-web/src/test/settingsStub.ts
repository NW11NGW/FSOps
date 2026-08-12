/**
 * The two GETs `SettingsProvider` fires on mount, in one place.
 *
 * Most components that show money, weights or durations call `useSettings`, so a test that mounts
 * one has to render it inside a real `SettingsProvider` - and that provider immediately fetches
 * `/settings` and `/settings/currencies`. Every such test was otherwise re-declaring the same
 * settings payload inline, which is both noise and a trap: a test that quietly stubbed a DIFFERENT
 * currency to the app's default would assert a formatted figure the user never sees.
 *
 * A real provider is used rather than a mocked `useSettings` on purpose. The formatting is the
 * part of a money figure most likely to be wrong, and stubbing `fmt` would assert the test's own
 * arithmetic instead of the app's.
 */
import type { AppSettings, CurrencyInfo } from '@/types/settings'

/** Matches `useSettings`' own DEFAULT_SETTINGS, so tests format the way a fresh install does. */
export const TEST_SETTINGS: AppSettings = {
  currencyCode: 'USD',
  distanceUnit: 'Nm',
  altitudeUnit: 'Feet',
  weightUnit: 'Kg',
  timeDisplay: 'Utc',
  use24HourClock: true,
  theme: 'dark',
  communityFolderPath: null,
  simBriefPilotId: null,
  vatsimCid: null,
}

export const TEST_CURRENCIES: CurrencyInfo[] = [
  { code: 'USD', symbol: '$', name: 'US Dollar', symbolBefore: true, decimalPlaces: 2, rate: 1 },
]

/**
 * The response for a settings path, or `undefined` when the path is not one of them - so a mocked
 * `get` can delegate here first and then handle its own component's endpoints:
 *
 * ```ts
 * vi.mocked(get).mockImplementation(async (path: string) => {
 *   const settings = settingsResponseFor(path)
 *   if (settings) return settings as never
 *   ...
 * })
 * ```
 */
export function settingsResponseFor(path: string): AppSettings | CurrencyInfo[] | undefined {
  if (path === '/settings') return TEST_SETTINGS
  if (path === '/settings/currencies') return TEST_CURRENCIES
  return undefined
}
