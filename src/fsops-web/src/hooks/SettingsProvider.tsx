import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'

import {
  DEFAULT_CURRENCY,
  DEFAULT_SETTINGS,
  SettingsContext,
  buildFormatters,
  normalizeCurrency,
  type Formatters,
  type RawCurrency,
  type SettingsContextValue,
  type SettingsStatus,
} from '@/hooks/useSettings'
import { ApiError, get, put } from '@/lib/api'
import type { AppSettings, CurrencyInfo } from '@/types/settings'

/**
 * Loads /settings and /settings/currencies once on mount and holds them for the whole tree.
 *
 * Its own file, separate from the `useSettings` hook it feeds: Vite's Fast Refresh only preserves
 * state for modules that export components and nothing else, so a file exporting both the provider
 * and the hook made every edit to it full-reload the page.
 */
export function SettingsProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<SettingsStatus>('loading')
  const [error, setError] = useState<string | null>(null)
  const [settings, setSettings] = useState<AppSettings>(DEFAULT_SETTINGS)
  const [currencies, setCurrencies] = useState<CurrencyInfo[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')
    setError(null)

    Promise.all([get<AppSettings>('/settings'), get<RawCurrency[]>('/settings/currencies')])
      .then(([settingsResult, currenciesResult]) => {
        if (cancelled) return
        setSettings(settingsResult)
        setCurrencies(currenciesResult.map(normalizeCurrency))
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setError(err instanceof ApiError ? err.message : 'Could not load settings. Check your connection.')
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  const updateSettings = useCallback(
    async (patch: Partial<AppSettings>) => {
      const previous = settings
      const next = { ...settings, ...patch }
      setSettings(next)
      try {
        const saved = await put<AppSettings>('/settings', next)
        setSettings(saved)
      } catch (err) {
        setSettings(previous)
        throw err
      }
    },
    [settings],
  )

  const currentCurrency = useMemo(
    () => currencies.find((c) => c.code === settings.currencyCode) ?? DEFAULT_CURRENCY,
    [currencies, settings.currencyCode],
  )

  const fmt = useMemo<Formatters>(
    () => buildFormatters(settings, currentCurrency),
    // Deliberately the individual unit settings rather than `settings` itself: the object identity
    // changes on every save, and rebuilding the formatters then would give every consumer a new
    // `fmt` for a currency-only change. Matches the set of fields buildFormatters actually reads.
    // eslint-disable-next-line react-hooks/exhaustive-deps -- see above
    [currentCurrency, settings.distanceUnit, settings.altitudeUnit, settings.weightUnit],
  )

  const value: SettingsContextValue = {
    status,
    error,
    settings,
    currencies,
    currentCurrency,
    updateSettings,
    refetch,
    fmt,
  }

  return <SettingsContext.Provider value={value}>{children}</SettingsContext.Provider>
}
