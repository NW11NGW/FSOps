export type DistanceUnit = 'Nm' | 'Km'
export type AltitudeUnit = 'Feet' | 'Metres'
export type WeightUnit = 'Kg' | 'Lb'
export type TimeDisplay = 'Utc' | 'Local'

export interface AppSettings {
  currencyCode: string
  distanceUnit: DistanceUnit
  altitudeUnit: AltitudeUnit
  weightUnit: WeightUnit
  timeDisplay: TimeDisplay
  use24HourClock: boolean
  theme: string
  communityFolderPath: string | null
  simBriefPilotId: string | null
}

export interface CurrencyInfo {
  code: string
  symbol: string
  name: string
  symbolBefore: boolean
  decimalPlaces: number
  rate: number
}
