import { useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Banknote, Building2, Plus } from 'lucide-react'

import { BuyLeaseDialog } from '@/components/fleet/BuyLeaseDialog'
import { FleetTable } from '@/components/fleet/FleetTable'
import { LoanDialog } from '@/components/fleet/LoanDialog'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleet } from '@/hooks/useFleet'
import type { LiveContext } from '@/types/live-context'

export function Fleet() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const fleetQuery = useFleet()

  const [buyLeaseOpen, setBuyLeaseOpen] = useState(false)
  const [loanOpen, setLoanOpen] = useState(false)

  function handleFleetChanged() {
    fleetQuery.refetch()
    airlineSummary.refetch()
  }

  if (airlineSummary.status === 'loading') {
    return (
      <div className="space-y-4">
        <PageHeader title="Fleet" description="Aircraft you own or lease, and their status." />
        <Skeleton className="h-72 w-full" />
      </div>
    )
  }

  if (airlineSummary.status === 'error' || !airlineSummary.data) {
    return (
      <div className="space-y-4">
        <PageHeader title="Fleet" description="Aircraft you own or lease, and their status." />
        <EmptyState icon={Building2} title="No airline yet" description="Set up your airline before managing a fleet." />
      </div>
    )
  }

  const groundedCount = fleetQuery.fleet.filter((a) => a.status === 'InMaintenance').length

  return (
    <div className="space-y-4">
      <PageHeader
        title="Fleet"
        description="Aircraft you own or lease, and their status."
        actions={
          <>
            <Button variant="outline" onClick={() => setLoanOpen(true)}>
              <Banknote />
              Take out a loan
            </Button>
            <Button onClick={() => setBuyLeaseOpen(true)}>
              <Plus />
              Add aircraft
            </Button>
          </>
        }
      />

      {groundedCount > 0 && (
        <div className="rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {groundedCount === 1
            ? '1 aircraft is grounded for maintenance.'
            : `${groundedCount} aircraft are grounded for maintenance.`}
        </div>
      )}

      <FleetTable
        fleet={fleetQuery.fleet}
        status={fleetQuery.status}
        emptyAction={
          <Button onClick={() => setBuyLeaseOpen(true)}>
            <Plus />
            Add aircraft
          </Button>
        }
      />

      <BuyLeaseDialog open={buyLeaseOpen} onOpenChange={setBuyLeaseOpen} onSuccess={handleFleetChanged} />
      <LoanDialog open={loanOpen} onOpenChange={setLoanOpen} onSuccess={handleFleetChanged} />
    </div>
  )
}
