import type { CurrencyInfo } from '@/types/settings'
import type { AltitudeUnit, DistanceUnit, WeightUnit } from '@/types/settings'

const NM_TO_KM = 1.852
const FT_TO_M = 0.3048
const KG_TO_LB = 2.20462

/**
 * Money is stored throughout the app in a single base unit and converted to
 * the user's chosen currency only at the point of display. `currency.rate`
 * is the multiplier from base units to that currency.
 */
export function formatMoney(baseAmount: number, currency: CurrencyInfo): string {
  const converted = baseAmount * currency.rate
  const magnitude = Math.abs(converted)
  const formatted = magnitude.toLocaleString('en-US', {
    minimumFractionDigits: currency.decimalPlaces,
    maximumFractionDigits: currency.decimalPlaces,
  })
  const sign = converted < 0 ? '-' : ''
  return currency.symbolBefore ? `${sign}${currency.symbol}${formatted}` : `${sign}${formatted}${currency.symbol}`
}

export function formatDistance(nm: number, unit: DistanceUnit): string {
  if (unit === 'Km') {
    return `${Math.round(nm * NM_TO_KM).toLocaleString('en-US')} km`
  }
  return `${Math.round(nm).toLocaleString('en-US')} nm`
}

export function formatAltitude(ft: number, unit: AltitudeUnit): string {
  if (unit === 'Metres') {
    return `${Math.round(ft * FT_TO_M).toLocaleString('en-US')} m`
  }
  return `${Math.round(ft).toLocaleString('en-US')} ft`
}

export function formatWeight(kg: number, unit: WeightUnit): string {
  if (unit === 'Lb') {
    return `${Math.round(kg * KG_TO_LB).toLocaleString('en-US')} lb`
  }
  return `${Math.round(kg).toLocaleString('en-US')} kg`
}

/** e.g. 105 -> "1h 45m", 40 -> "40m" */
export function formatDuration(minutes: number): string {
  const total = Math.max(0, Math.round(minutes))
  const hours = Math.floor(total / 60)
  const mins = total % 60
  if (hours === 0) return `${mins}m`
  return `${hours}h ${mins}m`
}

/** e.g. (51.4775, -0.4614) -> "51.4775°N, 0.4614°W" */
export function formatCoordinates(lat: number, lon: number): string {
  const latDirection = lat >= 0 ? 'N' : 'S'
  const lonDirection = lon >= 0 ? 'E' : 'W'
  return `${Math.abs(lat).toFixed(4)}°${latDirection}, ${Math.abs(lon).toFixed(4)}°${lonDirection}`
}
