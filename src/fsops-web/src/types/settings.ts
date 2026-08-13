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
  simBriefPilotId: string | null
  /** VATSIM CID (Certificate/Community ID) - stored locally like any other setting, read only
   *  server-side (see VatsimFlightCorroborationService). Never sent anywhere but back to FSOps'
   *  own API; the SPA never talks to VATSIM directly. Null disables G8/G9/G11's CID-specific
   *  behaviour entirely - the ATC layer and other-traffic layer still work with no CID at all. */
  vatsimCid: string | null
}

export interface CurrencyInfo {
  code: string
  symbol: string
  name: string
  symbolBefore: boolean
  decimalPlaces: number
  rate: number
}
