import { useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Banknote, Building2, Plus } from 'lucide-react'
import { toast } from 'sonner'

import { BuyLeaseDialog } from '@/components/fleet/BuyLeaseDialog'
import { FleetTable } from '@/components/fleet/FleetTable'
import { LoanDialog } from '@/components/fleet/LoanDialog'
import { RenameAircraftDialog } from '@/components/fleet/RenameAircraftDialog'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { useFleet } from '@/hooks/useFleet'
import { ApiError, put } from '@/lib/api'
import type { LiveContext } from '@/types/live-context'
import type { FleetAircraftSummary } from '@/types/fleet'

export function Fleet() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const fleetQuery = useFleet()

  const [buyLeaseOpen, setBuyLeaseOpen] = useState(false)
  const [loanOpen, setLoanOpen] = useState(false)
  const [renaming, setRenaming] = useState<FleetAircraftSummary | null>(null)
  const [confirmRelease, setConfirmRelease] = useState<FleetAircraftSummary | null>(null)
  const [reservationBusyId, setReservationBusyId] = useState<string | null>(null)

  function handleFleetChanged() {
    fleetQuery.refetch()
    airlineSummary.refetch()
  }

  const reservedCount = fleetQuery.fleet.filter((a) => a.reservedForPlayer).length

  async function applyReservation(aircraft: FleetAircraftSummary, reserved: boolean) {
    setReservationBusyId(aircraft.id)
    try {
      await put(`/fleet/${aircraft.id}/reservation`, { reserved })
      toast.success(
        reserved
          ? `${aircraft.registration} is now reserved for you to fly.`
          : `${aircraft.registration} is now available to virtual pilots.`,
      )
      fleetQuery.refetch()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not update this reservation. Try again.')
    } finally {
      setReservationBusyId(null)
    }
  }

  function handleToggleReservation(aircraft: FleetAircraftSummary) {
    if (reservationBusyId) return
    if (aircraft.reservedForPlayer && reservedCount <= 1) {
      // Releasing the last aircraft held back for the player - allowed, but warned clearly first;
      // see docs/PLAN.md "Releasing the last unreserved aircraft is allowed. Warn, do not forbid."
      setConfirmRelease(aircraft)
      return
    }
    void applyReservation(aircraft, !aircraft.reservedForPlayer)
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
        onRename={setRenaming}
        onToggleReservation={handleToggleReservation}
        emptyAction={
          <Button onClick={() => setBuyLeaseOpen(true)}>
            <Plus />
            Add aircraft
          </Button>
        }
      />

      <BuyLeaseDialog open={buyLeaseOpen} onOpenChange={setBuyLeaseOpen} onSuccess={handleFleetChanged} />
      <LoanDialog open={loanOpen} onOpenChange={setLoanOpen} onSuccess={handleFleetChanged} />
      <RenameAircraftDialog aircraft={renaming} onOpenChange={(open) => !open && setRenaming(null)} onSuccess={handleFleetChanged} />

      <Dialog open={confirmRelease !== null} onOpenChange={(open) => !open && setConfirmRelease(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Release {confirmRelease?.registration}?</DialogTitle>
            <DialogDescription>
              This is the only aircraft currently kept free for you to fly. Releasing it means every aircraft in
              your fleet becomes available to virtual pilots, and none will be held back for you — you can reserve
              one again any time from this page.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmRelease(null)}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={() => {
                if (confirmRelease) void applyReservation(confirmRelease, false)
                setConfirmRelease(null)
              }}
            >
              Release anyway
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
