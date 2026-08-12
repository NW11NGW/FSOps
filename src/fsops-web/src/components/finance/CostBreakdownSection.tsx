import type { ReactNode } from 'react'
import { Info, PieChart } from 'lucide-react'

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import type { FinanceCosts } from '@/types/finance'

interface CostBreakdownSectionProps {
  status: 'loading' | 'ready' | 'error'
  costs: FinanceCosts | null
  periodDays: number
}

function Line({ label, value, muted = false }: { label: string; value: string; muted?: boolean }) {
  return (
    <div className="flex items-center justify-between text-sm">
      <span className={muted ? 'text-muted-foreground' : ''}>{label}</span>
      <span className="tabular-nums">{value}</span>
    </div>
  )
}

function BreakdownCard({ title, icon, children, total }: { title: string; icon: ReactNode; children: ReactNode; total: ReactNode }) {
  return (
    <div className="rounded-md border border-border p-4">
      <div className="mb-3 flex items-center gap-2 text-sm font-medium">
        {icon}
        {title}
      </div>
      <div className="space-y-1.5">{children}</div>
      <div className="mt-3 flex items-center justify-between border-t border-border pt-2 text-sm font-semibold">
        <span>Total</span>
        {total}
      </div>
    </div>
  )
}

/**
 * The full breakdown of airline costs: fixed (leases, salaries, insurance, loan
 * repayments) split from variable (fuel, landing, handling, parking, passenger charges, turnaround,
 * maintenance, crew) because they behave completely differently - fixed is owed whether or not you
 * fly. `legacyDataNotice` is shown as a small, unalarming caveat rather than hidden or dramatised -
 * see types/finance.ts FinanceCosts doc.
 */
export function CostBreakdownSection({ status, costs, periodDays }: CostBreakdownSectionProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <PieChart className="size-4 text-muted-foreground" />
          Cost breakdown
        </CardTitle>
        <p className="text-xs text-muted-foreground">Last {periodDays} days, split fixed vs. variable, from posted ledger lines only.</p>
      </CardHeader>
      <CardContent>
        {status === 'loading' && (
          <div className="grid gap-3 sm:grid-cols-3">
            <Skeleton className="h-48 w-full" />
            <Skeleton className="h-48 w-full" />
            <Skeleton className="h-48 w-full" />
          </div>
        )}

        {status === 'error' && <p className="text-sm text-danger">Could not load your cost breakdown. Check your connection and try again.</p>}

        {status === 'ready' && costs && (
          <>
            <div className="grid gap-3 sm:grid-cols-3">
              <BreakdownCard title="Fixed costs" icon={<span className="size-2 rounded-full bg-warning" />} total={<span className="text-danger">{fmt.money(costs.fixed.total)}</span>}>
                <Line label="Lease payments" value={fmt.money(costs.fixed.leasePayments)} muted />
                <Line label="Salaries" value={fmt.money(costs.fixed.salaries)} muted />
                <Line label="Insurance" value={fmt.money(costs.fixed.insurance)} muted />
                <Line label="Loan payments" value={fmt.money(costs.fixed.loanPayments)} muted />
                {costs.fixed.leaseEarlyTermination !== 0 && <Line label="Lease early termination" value={fmt.money(costs.fixed.leaseEarlyTermination)} muted />}
                {costs.fixed.loanEarlySettlement !== 0 && <Line label="Loan early settlement" value={fmt.money(costs.fixed.loanEarlySettlement)} muted />}
              </BreakdownCard>

              <BreakdownCard title="Variable costs" icon={<span className="size-2 rounded-full bg-accent" />} total={<span className="text-danger">{fmt.money(costs.variable.total)}</span>}>
                <Line label="Fuel" value={fmt.money(costs.variable.fuel)} muted />
                <Line label="Landing fees" value={fmt.money(costs.variable.landingFees)} muted />
                <Line label="Handling" value={fmt.money(costs.variable.handling)} muted />
                <Line label="Parking fees" value={fmt.money(costs.variable.parkingFees)} muted />
                <Line label="Passenger charges" value={fmt.money(costs.variable.passengerCharges)} muted />
                <Line label="Turnaround fees" value={fmt.money(costs.variable.turnaroundFees)} muted />
                <Line label="Maintenance" value={fmt.money(costs.variable.maintenance)} muted />
                <Line label="Crew" value={fmt.money(costs.variable.crew)} muted />
                {costs.variable.cancellationFees !== 0 && <Line label="Cancellation fees" value={fmt.money(costs.variable.cancellationFees)} muted />}
                {costs.variable.other !== 0 && <Line label="Other" value={fmt.money(costs.variable.other)} muted />}
              </BreakdownCard>

              <BreakdownCard title="Revenue" icon={<span className="size-2 rounded-full bg-success" />} total={<span className="text-success">{fmt.money(costs.revenue.total)}</span>}>
                <Line label="Ticket revenue" value={fmt.money(costs.revenue.ticketRevenue)} muted />
                {costs.revenue.aircraftSaleProceeds !== 0 && <Line label="Aircraft sale proceeds" value={fmt.money(costs.revenue.aircraftSaleProceeds)} muted />}
              </BreakdownCard>
            </div>

            {costs.legacyDataNotice && (
              <div className="mt-3 flex items-start gap-2 rounded-md border border-border bg-muted/40 p-3 text-xs text-muted-foreground">
                <Info className="mt-0.5 size-3.5 shrink-0" />
                <span>{costs.legacyDataNotice}</span>
              </div>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}
