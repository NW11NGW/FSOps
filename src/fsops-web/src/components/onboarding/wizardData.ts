import { ACCENT_PALETTE } from '@/lib/accentPalette'
import type { AircraftFamily, CreateAirlineInput, Playstyle, StrategyProfile } from '@/types/airline'
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
  { key: 'playstyle', label: 'Playstyle' },
  { key: 'strategy', label: 'Strategy' },
  { key: 'aircraft', label: 'Aircraft' },
  { key: 'currency', label: 'Currency' },
  { key: 'review', label: 'Review' },
] as const

export type WizardStepKey = (typeof WIZARD_STEPS)[number]['key']

/**
 * Only genuinely editorial text - the name and a one-line tagline - lives here. Everything about
 * what a profile actually *does* (suggested fares, price sensitivity, load factor, costs, which
 * route advisories it raises) is fetched from GET /airline/strategy-profiles instead, so it can
 * never drift from the real economy-config.json values. See lib/strategyProfileCopy.ts and
 * hooks/useStrategyProfiles.ts.
 */
export interface StrategyProfileMeta {
  id: StrategyProfile
  label: string
  tagline: string
}

export const STRATEGY_PROFILES: StrategyProfileMeta[] = [
  {
    id: 'International',
    label: 'International',
    tagline: 'Long-haul network carrier',
  },
  {
    id: 'Domestic',
    label: 'Domestic',
    tagline: 'Short-haul regional workhorse',
  },
  {
    id: 'LowCost',
    label: 'Low-cost',
    tagline: 'High-frequency budget carrier',
  },
  {
    id: 'Premium',
    label: 'Premium',
    tagline: 'Business-focused yield leader',
  },
  {
    id: 'Balanced',
    label: 'Balanced',
    tagline: 'The all-rounder — flies anything, no restrictions',
  },
]

/**
 * Only genuinely editorial text lives here, same split as StrategyProfileMeta above - the actual
 * starting-capital/lease-deposit/starter-lease/insurance figures are fetched from
 * GET /airline/playstyles instead, so they can never drift from the real economy-config.json
 * values. See components/shared/PlaystyleCard.tsx and hooks/usePlaystyles.ts.
 */
export interface PlaystyleMeta {
  id: Playstyle
  label: string
  tagline: string
}

export const PLAYSTYLE_META: PlaystyleMeta[] = [
  { id: 'Casual', label: 'Casual', tagline: 'A growing airline in short, occasional sessions' },
  { id: 'TrueLife', label: 'True-life', tagline: 'Real-world figures — built around virtual pilots' },
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
  /** The player's own name for their founding pilot - optional, defaults sensibly server-side if left blank. */
  pilotName: string
  homeAirport: AirportSummary | null
  playstyle: Playstyle | null
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
}

export const DEFAULT_WIZARD_DATA: WizardData = {
  name: '',
  icaoCode: '',
  pilotName: '',
  homeAirport: null,
  playstyle: null,
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

export function isPlaystyleValid(data: WizardData): boolean {
  return data.playstyle !== null
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
  return data.loanAmount > 0 && data.loanTermMonths > 0
}

export const STEP_VALIDATORS: Record<WizardStepKey, (data: WizardData) => boolean> = {
  welcome: () => true,
  identity: isIdentityValid,
  homeBase: isHomeBaseValid,
  playstyle: isPlaystyleValid,
  strategy: isStrategyValid,
  aircraft: isAircraftValid,
  currency: isCurrencyValid,
  review: isFinanceValid,
}

export function buildCreateAirlineInput(data: WizardData): CreateAirlineInput {
  if (!data.homeAirport || !data.playstyle || !data.strategyProfile || !data.starterAircraftFamily) {
    throw new Error('Wizard data is incomplete.')
  }
  return {
    name: data.name.trim(),
    icaoCode: data.icaoCode,
    homeAirportIcao: data.homeAirport.icao,
    strategyProfile: data.strategyProfile,
    playstyle: data.playstyle,
    accentColour: data.accentColour,
    starterAircraftFamily: data.starterAircraftFamily,
    currencyCode: data.currencyCode,
    pilotName: data.pilotName.trim(),
    ...(data.loanEnabled
      ? {
          startingLoan: {
            amount: data.loanAmount,
            termMonths: data.loanTermMonths,
          },
        }
      : {}),
  }
}

/**
 * Standard amortising-loan monthly payment estimate for the live preview on the review step.
 * annualRatePct is always the server-computed figure from GET /airline/playstyles
 * (startingLoanAnnualRatePct) - never player-supplied - see docs/PLAN.md "Loan interest is set by
 * the simulation, never by the player".
 */
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
  if (lower.includes('playstyle')) return indexOf('playstyle')
  if (lower.includes('strategy')) return indexOf('strategy')
  if (lower.includes('aircraft') || lower.includes('colour') || lower.includes('color')) return indexOf('aircraft')
  if (lower.includes('currency')) return indexOf('currency')
  return indexOf('review')
}
