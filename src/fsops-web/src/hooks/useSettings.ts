import { createContext, useContext } from 'react'

import {
  formatAltitude,
  formatCoordinates,
  formatDistance,
  formatDuration,
  formatMoney,
  formatWeight,
} from '@/lib/format'
import type { AppSettings, CurrencyInfo } from '@/types/settings'

/*
 * The settings context and the hook onto it. `SettingsProvider` deliberately lives in its own file
 * (SettingsProvider.tsx) rather than here: a module that exports both a component and a hook
 * defeats Vite's Fast Refresh, which then full-reloads the page on every edit instead of preserving
 * state. Keeping this file component-free is what lets the ~50 modules that only want `useSettings`
 * go on importing it from exactly where they always have.
 */

export type SettingsStatus = 'loading' | 'ready' | 'error'

export const DEFAULT_CURRENCY: CurrencyInfo = {
  code: 'USD',
  symbol: '$',
  name: 'US Dollar',
  symbolBefore: true,
  decimalPlaces: 2,
  rate: 1,
}

export const DEFAULT_SETTINGS: AppSettings = {
  currencyCode: 'USD',
  distanceUnit: 'Nm',
  altitudeUnit: 'Feet',
  weightUnit: 'Kg',
  timeDisplay: 'Utc',
  use24HourClock: true,
  theme: 'dark',
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
export interface RawCurrency {
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

export function normalizeCurrency(raw: RawCurrency): CurrencyInfo {
  return {
    code: raw.code,
    symbol: raw.symbol,
    name: raw.name ?? raw.englishName ?? raw.code,
    symbolBefore: raw.symbolBefore ?? raw.placement !== 'After',
    decimalPlaces: raw.decimalPlaces,
    rate: raw.rate ?? raw.displayRate ?? 1,
  }
}

export interface Formatters {
  money: (baseAmount: number) => string
  distance: (nm: number) => string
  altitude: (ft: number) => string
  weight: (kg: number) => string
  duration: (minutes: number) => string
  coordinates: (lat: number, lon: number) => string
}

export function buildFormatters(settings: AppSettings, currency: CurrencyInfo): Formatters {
  return {
    money: (baseAmount: number) => formatMoney(baseAmount, currency),
    distance: (nm: number) => formatDistance(nm, settings.distanceUnit),
    altitude: (ft: number) => formatAltitude(ft, settings.altitudeUnit),
    weight: (kg: number) => formatWeight(kg, settings.weightUnit),
    duration: (minutes: number) => formatDuration(minutes),
    coordinates: (lat: number, lon: number) => formatCoordinates(lat, lon),
  }
}

export interface SettingsContextValue {
  status: SettingsStatus
  error: string | null
  settings: AppSettings
  currencies: CurrencyInfo[]
  currentCurrency: CurrencyInfo
  updateSettings: (patch: Partial<AppSettings>) => Promise<void>
  refetch: () => void
  fmt: Formatters
}

export const SettingsContext = createContext<SettingsContextValue | null>(null)

/** Loaded settings, the currency catalogue, an optimistic mutation, and ready-bound formatters. */
export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext)
  if (!ctx) throw new Error('useSettings must be used within a SettingsProvider')
  return ctx
}
