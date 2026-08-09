import { useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Building2, DollarSign, TrendingDown, TrendingUp, Wallet } from 'lucide-react'

import { EndLeaseDialog } from '@/components/fleet/EndLeaseDialog'
import { CostBreakdownSection } from '@/components/finance/CostBreakdownSection'
import { LedgerSection } from '@/components/finance/LedgerSection'
import { LeasesSection } from '@/components/finance/LeasesSection'
import { LoanRepaymentDialog } from '@/components/finance/LoanRepaymentDialog'
import { LoansSection } from '@/components/finance/LoansSection'
import { PilotsFinanceSection } from '@/components/finance/PilotsFinanceSection'
import { RoutesPnlSection } from '@/components/finance/RoutesPnlSection'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { StatTile } from '@/components/shared/StatTile'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useFinanceOverview } from '@/hooks/useFinanceOverview'
import { useSettings } from '@/hooks/useSettings'
import { formatDate } from '@/lib/format'
import type { LiveContext } from '@/types/live-context'
import type { FinanceLease, FinanceLoan } from '@/types/finance'

const PERIOD_DAYS = 30

export function Finances() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const overview = useFinanceOverview(PERIOD_DAYS)
  const { fmt } = useSettings()

  const [endingLease, setEndingLease] = useState<FinanceLease | null>(null)
  const [repayingLoan, setRepayingLoan] = useState<FinanceLoan | null>(null)

  function handleChanged() {
    overview.refetch()
    airlineSummary.refetch()
  }

  if (airlineSummary.status === 'loading') {
    return (
      <div className="space-y-4">
        <PageHeader title="Finances" description="Cash flow, loans, and the ledger behind your airline's economy." />
      </div>
    )
  }

  if (airlineSummary.status === 'error' || !airlineSummary.data) {
    return (
      <div className="space-y-4">
        <PageHeader title="Finances" description="Cash flow, loans, and the ledger behind your airline's economy." />
        <EmptyState icon={Building2} title="No airline yet" description="Set up your airline before there is anything to show here." />
      </div>
    )
  }

  const summary = overview.summary
  const loading = overview.status === 'loading'
  const cashDelta = summary ? summary.cashBalance - summary.cashBalance30DaysAgo : 0

  return (
    <div className="space-y-4">
      <PageHeader title="Finances" description="Cash flow, leases, loans, and the ledger behind your airline's economy." />

      {overview.status === 'error' && (
        <div className="rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          Could not load your finances. Check your connection and try again.
        </div>
      )}

      <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-4">
        <StatTile
          label="Cash balance"
          value={summary ? fmt.money(summary.cashBalance) : undefined}
          icon={Wallet}
          loading={loading}
          trend={
            summary
              ? {
                  direction: cashDelta > 0 ? 'up' : cashDelta < 0 ? 'down' : 'flat',
                  label: `${cashDelta >= 0 ? '+' : ''}${fmt.money(cashDelta)} over 30 days`,
                }
              : undefined
          }
        />
        <StatTile
          label="Period income"
          value={summary ? fmt.money(summary.currentPeriodIncome) : undefined}
          icon={TrendingUp}
          loading={loading}
        />
        <StatTile
          label="Period expenditure"
          value={summary ? fmt.money(summary.currentPeriodExpenditure) : undefined}
          icon={TrendingDown}
          loading={loading}
        />
        <StatTile
          label="Period profit/loss"
          value={summary ? fmt.money(summary.currentPeriodNet) : undefined}
          icon={DollarSign}
          loading={loading}
          trend={
            summary
              ? {
                  direction: summary.currentPeriodNet > 0 ? 'up' : summary.currentPeriodNet < 0 ? 'down' : 'flat',
                  label: summary.currentPeriodStartUtc
                    ? `${formatDate(summary.currentPeriodStartUtc)} – ${formatDate(summary.currentPeriodEndUtc)} (30-day rolling cycle)`
                    : 'No billing cycle has run yet',
                }
              : undefined
          }
        />
      </div>

      <Tabs defaultValue="leases">
        <TabsList>
          <TabsTrigger value="leases">Leases</TabsTrigger>
          <TabsTrigger value="loans">Loans</TabsTrigger>
          <TabsTrigger value="pilots">Pilots</TabsTrigger>
          <TabsTrigger value="costs">Costs</TabsTrigger>
          <TabsTrigger value="routes">Routes</TabsTrigger>
          <TabsTrigger value="ledger">Ledger</TabsTrigger>
        </TabsList>

        <TabsContent value="leases">
          <LeasesSection
            status={overview.status}
            leases={overview.leases.leases}
            totalMonthlyCommitment={overview.leases.totalMonthlyCommitment}
            onEndLease={setEndingLease}
          />
        </TabsContent>

        <TabsContent value="loans">
          <LoansSection status={overview.status} loans={overview.loans} onRepay={setRepayingLoan} />
        </TabsContent>

        <TabsContent value="pilots">
          <PilotsFinanceSection status={overview.status} pilots={overview.pilots} periodDays={overview.periodDays} />
        </TabsContent>

        <TabsContent value="costs">
          <CostBreakdownSection status={overview.status} costs={overview.costs} periodDays={overview.periodDays} />
        </TabsContent>

        <TabsContent value="routes">
          <RoutesPnlSection status={overview.status} routes={overview.routes} periodDays={overview.periodDays} />
        </TabsContent>

        <TabsContent value="ledger">
          <LedgerSection />
        </TabsContent>
      </Tabs>

      <EndLeaseDialog
        target={endingLease ? { id: endingLease.fleetAircraftId, registration: endingLease.registration } : null}
        onOpenChange={(open) => !open && setEndingLease(null)}
        onSuccess={handleChanged}
      />
      <LoanRepaymentDialog loan={repayingLoan} onOpenChange={(open) => !open && setRepayingLoan(null)} onSuccess={handleChanged} />
    </div>
  )
}
