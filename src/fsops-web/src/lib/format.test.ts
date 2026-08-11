import { describe, expect, it } from 'vitest'

import { formatDate, formatDateTime, formatDuration, formatMoney } from '@/lib/format'
import type { CurrencyInfo } from '@/types/settings'

const USD: CurrencyInfo = { code: 'USD', symbol: '$', name: 'US Dollar', symbolBefore: true, decimalPlaces: 2, rate: 1 }
const GBP: CurrencyInfo = { code: 'GBP', symbol: '£', name: 'Pound Sterling', symbolBefore: true, decimalPlaces: 2, rate: 0.79 }
const JPY: CurrencyInfo = { code: 'JPY', symbol: '¥', name: 'Japanese Yen', symbolBefore: true, decimalPlaces: 0, rate: 149.2 }
// A currency whose symbol trails the amount, e.g. many European conventions render "1.234,56 kr".
const SEK: CurrencyInfo = { code: 'SEK', symbol: 'kr', name: 'Swedish Krona', symbolBefore: false, decimalPlaces: 2, rate: 10.4 }

describe('formatMoney', () => {
  it('respects the user-selected currency rather than a hardcoded symbol', () => {
    // Same base amount, three different currencies - the symbol and converted figure must both
    // track the caller's chosen currency, not default to USD/$.
    expect(formatMoney(10000, USD)).toBe('$10,000.00')
    expect(formatMoney(10000, GBP)).toBe('£7,900.00')
    expect(formatMoney(10000, JPY)).toBe('¥1,492,000')
  })

  it('places the symbol after the amount when the currency says so', () => {
    expect(formatMoney(1000, SEK)).toBe('10,400.00kr')
  })

  it('honours the currency decimal-places setting (JPY has none)', () => {
    expect(formatMoney(1, JPY)).toBe('¥149')
  })

  it('puts the minus sign before the symbol for a negative amount', () => {
    expect(formatMoney(-5000, USD)).toBe('-$5,000.00')
  })

  it('formats zero without a sign', () => {
    expect(formatMoney(0, USD)).toBe('$0.00')
  })
})

describe('formatDate', () => {
  it('formats a real ISO date', () => {
    // toLocaleDateString's field order depends on the runtime's default locale, which varies by
    // machine/CI - assert the day/month/year values are all present rather than one fixed string
    // ordering, so this test doesn't flake between environments.
    const result = formatDate('2026-08-31T00:00:00Z')
    expect(result).toContain('31')
    expect(result).toContain('Aug')
    expect(result).toContain('2026')
  })

  it('returns an em dash for null, undefined, and unparseable input rather than "Invalid Date"', () => {
    expect(formatDate(null)).toBe('—')
    expect(formatDate(undefined)).toBe('—')
    expect(formatDate('not-a-date')).toBe('—')
  })
})

describe('formatDateTime', () => {
  it('includes both the date and the time', () => {
    const result = formatDateTime('2026-08-31T14:32:00Z')
    expect(result).toContain('31 Aug 2026')
    expect(result).toMatch(/\d{2}:\d{2}/)
  })

  it('returns an em dash for null/unparseable input', () => {
    expect(formatDateTime(null)).toBe('—')
    expect(formatDateTime('garbage')).toBe('—')
  })
})

describe('formatDuration', () => {
  it('renders sub-hour durations as minutes only', () => {
    expect(formatDuration(40)).toBe('40m')
  })

  it('renders durations of an hour or more as hours and minutes', () => {
    expect(formatDuration(105)).toBe('1h 45m')
  })

  it('floors negative input at zero', () => {
    expect(formatDuration(-10)).toBe('0m')
  })
})
