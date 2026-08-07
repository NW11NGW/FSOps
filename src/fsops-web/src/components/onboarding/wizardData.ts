import { ACCENT_PALETTE } from '@/lib/accentPalette'
import type { AircraftFamily, CreateAirlineInput, StrategyProfile } from '@/types/airline'
import type { AirportSummary } from '@/types/airport'
import type { AltitudeUnit, CurrencyInfo, DistanceUnit, TimeDisplay, WeightUnit } from '@/types/settings'

/** Used when the currency catalogue hasn't loaded yet, so money in the wizard never silently disappears. */
export const FALLBACK_CURRENCY: CurrencyInfo = {
  code: 'USD',
  symbol: '$',
  name: 'US Dollar',
  symbolBefore: true,
  decimalPlaces: 2,
  rate: 1,
}

export const WIZARD_STEPS = [
  { key: 'welcome', label: 'Welcome' },
  { key: 'identity', label: 'Identity' },
  { key: 'homeBase', label: 'Home base' },
  { key: 'strategy', label: 'Strategy' },
  { key: 'aircraft', label: 'Aircraft' },
  { key: 'currency', label: 'Currency' },
  { key: 'review', label: 'Review' },
] as const

export type WizardStepKey = (typeof WIZARD_STEPS)[number]['key']

export interface StrategyProfileMeta {
  id: StrategyProfile
  label: string
  tagline: string
  routeLengths: string
  fares: string
  costs: string
}

export const STRATEGY_PROFILES: StrategyProfileMeta[] = [
  {
    id: 'International',
    label: 'International',
    tagline: 'Long-haul network carrier',
    routeLengths: 'Long-haul routes, 2,000+ nm — think intercontinental flying.',
    fares: 'Higher fares, priced for range and prestige.',
    costs: 'Higher fuel and crew costs; suits wide-body-friendly economics.',
  },
  {
    id: 'Domestic',
    label: 'Domestic',
    tagline: 'Short-haul regional workhorse',
    routeLengths: 'Short-to-medium routes within a country or region.',
    fares: 'Moderate fares with frequent, reliable service.',
    costs: 'Lower per-flight costs; fast aircraft turnaround and utilisation.',
  },
  {
    id: 'LowCost',
    label: 'Low-cost',
    tagline: 'High-frequency budget carrier',
    routeLengths: 'Short routes, flown often, point-to-point.',
    fares: 'The lowest fares in the market — volume over margin.',
    costs: 'Tight cost control everywhere; every empty seat hurts.',
  },
  {
    id: 'Premium',
    label: 'Premium',
    tagline: 'Business-focused yield leader',
    routeLengths: 'Medium-to-long routes between major business hubs.',
    fares: 'Premium fares from a smaller, higher-yield passenger base.',
    costs: 'Higher service costs, offset by fewer but better-paying seats.',
  },
]

export interface AircraftMeta {
  id: AircraftFamily
  label: string
  manufacturer: string
  pax: string
  range: string
  cruise: string
}

export const AIRCRAFT_OPTIONS: AircraftMeta[] = [
  {
    id: 'A320',
    label: 'Airbus A320',
    manufacturer: 'Airbus',
    pax: '~180 seats',
    range: '~3,300 nm',
    cruise: 'Mach 0.78 (~450 kt)',
  },
  {
    id: 'B737',
    label: 'Boeing 737-800',
    manufacturer: 'Boeing',
    pax: '~189 seats',
    range: '~2,935 nm',
    cruise: 'Mach 0.785 (~453 kt)',
  },
]

export interface WizardData {
  name: string
  icaoCode: string
  homeAirport: AirportSummary | null
  strategyProfile: StrategyProfile | null
  accentColour: string
  starterAircraftFamily: AircraftFamily | null
  currencyCode: string
  distanceUnit: DistanceUnit
  altitudeUnit: AltitudeUnit
  weightUnit: WeightUnit
  timeDisplay: TimeDisplay
  use24HourClock: boolean
  loanEnabled: boolean
  loanAmount: number
  loanTermMonths: number
  loanRatePct: number
}

export const DEFAULT_WIZARD_DATA: WizardData = {
  name: '',
  icaoCode: '',
  homeAirport: null,
  strategyProfile: null,
  accentColour: (ACCENT_PALETTE[0] ?? { name: 'Sky', hex: '#0EA5E9' }).hex,
  starterAircraftFamily: null,
  currencyCode: 'USD',
  distanceUnit: 'Nm',
  altitudeUnit: 'Feet',
  weightUnit: 'Kg',
  timeDisplay: 'Utc',
  use24HourClock: true,
  loanEnabled: false,
  loanAmount: 5_000_000,
  loanTermMonths: 60,
  loanRatePct: 6,
}

const ICAO_PATTERN = /^[A-Z]{2,3}$/
const HEX_PATTERN = /^#[0-9a-fA-F]{6}$/

export function isIdentityValid(data: WizardData): boolean {
  const name = data.name.trim()
  return name.length >= 2 && name.length <= 40 && ICAO_PATTERN.test(data.icaoCode)
}

export function isHomeBaseValid(data: WizardData): boolean {
  return data.homeAirport !== null
}

export function isStrategyValid(data: WizardData): boolean {
  return data.strategyProfile !== null
}

export function isAircraftValid(data: WizardData): boolean {
  return HEX_PATTERN.test(data.accentColour) && data.starterAircraftFamily !== null
}

export function isCurrencyValid(data: WizardData): boolean {
  return data.currencyCode.trim().length > 0
}

export function isFinanceValid(data: WizardData): boolean {
  if (!data.loanEnabled) return true
  return data.loanAmount > 0 && data.loanTermMonths > 0 && data.loanRatePct >= 0
}

export const STEP_VALIDATORS: Record<WizardStepKey, (data: WizardData) => boolean> = {
  welcome: () => true,
  identity: isIdentityValid,
  homeBase: isHomeBaseValid,
  strategy: isStrategyValid,
  aircraft: isAircraftValid,
  currency: isCurrencyValid,
  review: isFinanceValid,
}

export function buildCreateAirlineInput(data: WizardData): CreateAirlineInput {
  if (!data.homeAirport || !data.strategyProfile || !data.starterAircraftFamily) {
    throw new Error('Wizard data is incomplete.')
  }
  return {
    name: data.name.trim(),
    icaoCode: data.icaoCode,
    homeAirportIcao: data.homeAirport.icao,
    strategyProfile: data.strategyProfile,
    accentColour: data.accentColour,
    starterAircraftFamily: data.starterAircraftFamily,
    currencyCode: data.currencyCode,
    ...(data.loanEnabled
      ? {
          startingLoan: {
            amount: data.loanAmount,
            termMonths: data.loanTermMonths,
            annualRatePct: data.loanRatePct,
          },
        }
      : {}),
  }
}

/** Standard amortising-loan monthly payment estimate for the live preview on the review step. */
export function estimateMonthlyPayment(amount: number, termMonths: number, annualRatePct: number): number {
  if (amount <= 0 || termMonths <= 0) return 0
  const monthlyRate = annualRatePct / 100 / 12
  if (monthlyRate === 0) return amount / termMonths
  const factor = Math.pow(1 + monthlyRate, termMonths)
  return (amount * monthlyRate * factor) / (factor - 1)
}

/** Best-effort mapping from a backend validation message onto the step that most likely caused it. */
export function resolveErrorStepIndex(message: string): number {
  const lower = message.toLowerCase()
  const indexOf = (key: WizardStepKey) => WIZARD_STEPS.findIndex((s) => s.key === key)

  if (lower.includes('icao') || lower.includes('name')) return indexOf('identity')
  if (lower.includes('airport') || lower.includes('home base') || lower.includes('hub')) return indexOf('homeBase')
  if (lower.includes('strategy')) return indexOf('strategy')
  if (lower.includes('aircraft') || lower.includes('colour') || lower.includes('color')) return indexOf('aircraft')
  if (lower.includes('currency')) return indexOf('currency')
  return indexOf('review')
}
