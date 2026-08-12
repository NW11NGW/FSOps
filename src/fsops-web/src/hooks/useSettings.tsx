import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

import { ApiError, get, put } from '@/lib/api'
import {
  formatAltitude,
  formatCoordinates,
  formatDistance,
  formatDuration,
  formatMoney,
  formatWeight,
} from '@/lib/format'
import type { AppSettings, CurrencyInfo } from '@/types/settings'

export type SettingsStatus = 'loading' | 'ready' | 'error'

const DEFAULT_CURRENCY: CurrencyInfo = {
  code: 'USD',
  symbol: '$',
  name: 'US Dollar',
  symbolBefore: true,
  decimalPlaces: 2,
  rate: 1,
}

const DEFAULT_SETTINGS: AppSettings = {
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

/**
 * The documented contract for /settings/currencies is
 * `{ code, symbol, name, symbolBefore, decimalPlaces, rate }`, but tolerate
 * the field names the live backend actually ships today
 * (`englishName` / `placement: "Before"|"After"` / `displayRate`) so a
 * naming drift on either side doesn't silently render "NaN" in the UI.
 */
interface RawCurrency {
  code: string
  symbol: string
  name?: string
  englishName?: string
  symbolBefore?: boolean
  placement?: 'Before' | 'After'
  decimalPlaces: number
  rate?: number
  displayRate?: number
}

function normalizeCurrency(raw: RawCurrency): CurrencyInfo {
  return {
    code: raw.code,
    symbol: raw.symbol,
    name: raw.name ?? raw.englishName ?? raw.code,
    symbolBefore: raw.symbolBefore ?? raw.placement !== 'After',
    decimalPlaces: raw.decimalPlaces,
    rate: raw.rate ?? raw.displayRate ?? 1,
  }
}

interface Formatters {
  money: (baseAmount: number) => string
  distance: (nm: number) => string
  altitude: (ft: number) => string
  weight: (kg: number) => string
  duration: (minutes: number) => string
  coordinates: (lat: number, lon: number) => string
}

interface SettingsContextValue {
  status: SettingsStatus
  error: string | null
  settings: AppSettings
  currencies: CurrencyInfo[]
  currentCurrency: CurrencyInfo
  updateSettings: (patch: Partial<AppSettings>) => Promise<void>
  refetch: () => void
  fmt: Formatters
}

const SettingsContext = createContext<SettingsContextValue | null>(null)

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
    () => ({
      money: (baseAmount: number) => formatMoney(baseAmount, currentCurrency),
      distance: (nm: number) => formatDistance(nm, settings.distanceUnit),
      altitude: (ft: number) => formatAltitude(ft, settings.altitudeUnit),
      weight: (kg: number) => formatWeight(kg, settings.weightUnit),
      duration: (minutes: number) => formatDuration(minutes),
      coordinates: (lat: number, lon: number) => formatCoordinates(lat, lon),
    }),
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

/** Loaded settings, the currency catalogue, an optimistic mutation, and ready-bound formatters. */
export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext)
  if (!ctx) throw new Error('useSettings must be used within a SettingsProvider')
  return ctx
}
