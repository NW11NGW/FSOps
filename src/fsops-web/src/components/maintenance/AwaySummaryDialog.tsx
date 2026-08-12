import { Link } from 'react-router-dom'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Separator } from '@/components/ui/separator'
import { useAwaySummary } from '@/hooks/useAwaySummary'
import { useSettings } from '@/hooks/useSettings'
import type { UnflyableOccurrenceSummary } from '@/types/maintenance'

/** e.g. 2.5 -> "2h 30m", 76 -> "3 days 4h" - deliberately coarser than formatDuration (which is
 *  built for a flight's block time in minutes), since an away-gap is usually measured in days. */
function formatAwayDuration(hours: number): string {
  if (hours < 1) return `${Math.round(hours * 60)}m`
  if (hours < 24) {
    const wholeHours = Math.floor(hours)
    const minutes = Math.round((hours - wholeHours) * 60)
    return minutes > 0 ? `${wholeHours}h ${minutes}m` : `${wholeHours}h`
  }
  const days = Math.floor(hours / 24)
  const remainingHours = Math.round(hours % 24)
  return remainingHours > 0 ? `${days} day${days === 1 ? '' : 's'} ${remainingHours}h` : `${days} day${days === 1 ? '' : 's'}`
}

const CATEGORY_LABELS: Record<string, string> = {
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
  LeasePayment: 'Lease payment',
  LeaseDeposit: 'Lease deposit',
  LoanPayment: 'Loan payment',
  AircraftPurchase: 'Aircraft purchase/sale',
  Insurance: 'Insurance',
  GsxServices: 'GSX services',
  StartingCapital: 'Starting capital',
  LoanProceeds: 'Loan proceeds',
  CancellationFee: 'Cancellation fees',
  Other: 'Other',
}

function unresolvedStatusLabel(status: UnflyableOccurrenceSummary['status']): { label: string; variant: 'muted' | 'warning' | 'danger' } {
  if (status === 'Suspended') return { label: 'Suspended (maintenance)', variant: 'muted' }
  if (status === 'Cancelled') return { label: 'Cancelled', variant: 'danger' }
  return { label: 'Skipped', variant: 'warning' }
}

/**
 * "While you were away" - the app has to explain catch-up, or months of bills arriving at once
 * looks exactly like a bug. On startup after a real
 * gap, shows what the wall clock resolved while the player was gone - charges by category, what
 * virtual pilots flew and earned, maintenance that fell due, and anything skipped/cancelled/
 * suspended and why - rather than opening to a changed balance with no explanation. Self-contained:
 * fetches its own data via useAwaySummary and renders nothing at all when there's genuinely
 * nothing to report, so it's safe to mount unconditionally (e.g. on the Dashboard).
 */
export function AwaySummaryDialog() {
  const { summary, acknowledge } = useAwaySummary()
  const { fmt } = useSettings()

  if (!summary) {
    return null
  }

  const netIsPositive = summary.netLedgerChange >= 0

  return (
    <Dialog open onOpenChange={(next) => !next && acknowledge()}>
      <DialogContent className="max-h-[85vh] max-w-lg overflow-y-auto">
        <DialogHeader>
          <DialogTitle>While you were away</DialogTitle>
          <DialogDescription>
            The airline kept running on the wall clock - closed for {formatAwayDuration(summary.awayHours)}. Here's what happened.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="flex items-center justify-between rounded-md border border-border p-3">
            <div>
              <p className="text-xs text-muted-foreground">Net effect</p>
              <p className={`text-lg font-semibold tabular-nums ${netIsPositive ? 'text-success' : 'text-danger'}`}>
                {netIsPositive ? '+' : ''}
                {fmt.money(summary.netLedgerChange)}
              </p>
            </div>
            <div className="text-right">
              <p className="text-xs text-muted-foreground">Cash balance now</p>
              <p className="text-lg font-semibold tabular-nums">{fmt.money(summary.cashBalance)}</p>
            </div>
          </div>

          {summary.chargesByCategory.length > 0 && (
            <div className="space-y-1.5">
              <p className="text-xs font-medium text-muted-foreground">Charges by category</p>
              <div className="space-y-1 rounded-md border border-border p-3 text-sm">
                {summary.chargesByCategory.map((c) => (
                  <div key={c.category} className="flex items-center justify-between">
                    <span className="text-muted-foreground">
                      {CATEGORY_LABELS[c.category] ?? c.category}
                      {c.count > 1 ? ` (${c.count})` : ''}
                    </span>
                    <span className={`tabular-nums ${c.amount >= 0 ? 'text-success' : ''}`}>{fmt.money(c.amount)}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {summary.virtualPilots.sectorsFlown > 0 && (
            <div className="space-y-1.5">
              <p className="text-xs font-medium text-muted-foreground">Virtual pilots</p>
              <div className="space-y-1 rounded-md border border-border p-3 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Sectors flown</span>
                  <span className="tabular-nums">{summary.virtualPilots.sectorsFlown}</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Revenue</span>
                  <span className="tabular-nums text-success">{fmt.money(summary.virtualPilots.revenue)}</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Cost</span>
                  <span className="tabular-nums">{fmt.money(summary.virtualPilots.cost)}</span>
                </div>
                <div className="flex items-center justify-between border-t border-border pt-1 font-medium">
                  <span>Net</span>
                  <span className={`tabular-nums ${summary.virtualPilots.net >= 0 ? 'text-success' : 'text-danger'}`}>
                    {fmt.money(summary.virtualPilots.net)}
                  </span>
                </div>
              </div>
            </div>
          )}

          {summary.maintenanceEvents.length > 0 && (
            <div className="space-y-1.5">
              <p className="text-xs font-medium text-muted-foreground">Maintenance that fell due</p>
              <div className="space-y-1.5">
                {summary.maintenanceEvents.map((m, i) => (
                  <div key={i} className="flex items-center justify-between rounded-md border border-border p-2 text-sm">
                    <span>
                      {m.registration} - {m.type === 'CCheck' ? 'C-check' : 'A-check'}
                    </span>
                    <span className="tabular-nums text-muted-foreground">{fmt.money(m.cost)}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {summary.unresolvedOccurrences.length > 0 && (
            <div className="space-y-1.5">
              <p className="text-xs font-medium text-muted-foreground">Skipped, cancelled, or suspended</p>
              <div className="space-y-1.5">
                {summary.unresolvedOccurrences.map((o) => {
                  const { label, variant } = unresolvedStatusLabel(o.status)
                  return (
                    <div key={o.flightId} className="rounded-md border border-border p-2 text-sm">
                      <div className="flex items-center justify-between gap-2">
                        <span className="font-medium">{o.routeLabel}</span>
                        <Badge variant={variant}>{label}</Badge>
                      </div>
                      {o.reason && <p className="mt-1 text-xs text-muted-foreground">{o.reason}</p>}
                    </div>
                  )
                })}
              </div>
            </div>
          )}

          <Separator />
          <Link to="/finances" onClick={acknowledge} className="text-sm text-accent underline-offset-4 hover:underline">
            View the full ledger →
          </Link>
        </div>

        <DialogFooter>
          <Button onClick={acknowledge}>Got it</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
