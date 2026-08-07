/**
 * Curated accent-colour swatches offered in the onboarding wizard and the
 * Airline settings section. These are user-facing colour data (the one
 * legitimate exception to "no hardcoded hex"), defined once here so both
 * surfaces stay in sync.
 */
export interface AccentSwatch {
  name: string
  hex: string
}

export const ACCENT_PALETTE: readonly AccentSwatch[] = [
  { name: 'Sky', hex: '#0EA5E9' },
  { name: 'Cobalt', hex: '#2563EB' },
  { name: 'Teal', hex: '#0D9488' },
  { name: 'Emerald', hex: '#059669' },
  { name: 'Amber', hex: '#D97706' },
  { name: 'Crimson', hex: '#DC2626' },
  { name: 'Rose', hex: '#E11D48' },
  { name: 'Violet', hex: '#7C3AED' },
  { name: 'Slate', hex: '#475569' },
]
