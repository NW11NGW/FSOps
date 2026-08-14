import type { BadgeProps } from '@/components/ui/badge'

/**
 * Every LedgerCategory the backend can post (src/FSOps.Core/Entities/Enums.cs), in the order the
 * ledger filter dropdown offers them. Kept as a plain list rather than importing a generated enum
 * since the frontend has no codegen step from the C# entities - see types/flight.ts LedgerCategory
 * for the same list already declared there (not reused directly: that file is out of scope to
 * import from for finance work per the ownership split, and duplicating a string union costs
 * nothing).
 */
export const LEDGER_CATEGORIES = [
  'TicketRevenue',
  'Fuel',
  'LandingFees',
  'Handling',
  'Maintenance',
  'Salary',
  'CrewCost',
  'ParkingFees',
  'PassengerCharges',
  'TurnaroundFees',
  'CancellationFee',
  'AircraftRepositioning',
  'VatsimOnlineBonus',
  'LeasePayment',
  'LoanPayment',
  'AircraftPurchase',
  'Insurance',
  'StartingCapital',
  'LoanProceeds',
  'Other',
] as const

const LABELS: Record<string, string> = {
  TicketRevenue: 'Ticket revenue',
  Fuel: 'Fuel',
  LandingFees: 'Landing fees',
  Handling: 'Handling',
  Maintenance: 'Maintenance',
  Salary: 'Salary',
  CrewCost: 'Crew cost',
  ParkingFees: 'Parking fees',
  PassengerCharges: 'Passenger charges',
  TurnaroundFees: 'Turnaround fees',
  CancellationFee: 'Cancellation fee',
  AircraftRepositioning: 'Aircraft repositioning',
  VatsimOnlineBonus: 'Online flying bonus',
  LeasePayment: 'Lease payment',
  LoanPayment: 'Loan payment',
  AircraftPurchase: 'Aircraft purchase/sale',
  Insurance: 'Insurance',
  StartingCapital: 'Starting capital',
  LoanProceeds: 'Loan proceeds',
  Other: 'Other',
}

/** e.g. "LandingFees" -> "Landing fees". Falls back to the raw category if unrecognised, so an
 *  unknown value from the server still renders something rather than blank. */
export function ledgerCategoryLabel(category: string): string {
  return LABELS[category] ?? category
}

/** Income categories get a success badge, everything else (all cost categories) muted - the sign
 *  of the actual amount is what tells the real income/expense story on each row, this is just a
 *  quick visual grouping for the category chip itself. */
export function ledgerCategoryBadgeVariant(category: string): NonNullable<BadgeProps['variant']> {
  if (
    category === 'TicketRevenue' ||
    category === 'StartingCapital' ||
    category === 'LoanProceeds' ||
    category === 'VatsimOnlineBonus'
  ) {
    return 'success'
  }
  return 'muted'
}
