import { LogOut, FileClock } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useSettings } from '@/hooks/useSettings'
import { formatDate } from '@/lib/format'
import type { FinanceLease } from '@/types/finance'

interface LeasesSectionProps {
  status: 'loading' | 'ready' | 'error'
  leases: FinanceLease[]
  totalMonthlyCommitment: number
  onEndLease: (lease: FinanceLease) => void
}

/**
 * Leases, with dates and an exit: every active lease, its monthly cost, and when
 * the next payment is actually due - a real date derived from the airline's rolling 30-day billing
 * clock, never "the 1st of the month". End-lease opens the shared EndLeaseDialog (components/fleet).
 */
export function LeasesSection({ status, leases, totalMonthlyCommitment, onEndLease }: LeasesSectionProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between space-y-0 pb-3">
        <div>
          <CardTitle className="flex items-center gap-2 text-base">
            <FileClock className="size-4 text-muted-foreground" />
            Leases
          </CardTitle>
          <p className="mt-1 text-xs text-muted-foreground">
            Billed on a rolling 30-day cycle from your airline's own clock, not the calendar.
          </p>
        </div>
        {status === 'ready' && leases.length > 0 && (
          <div className="text-right">
            <p className="text-xs text-muted-foreground">Total monthly commitment</p>
            <p className="font-semibold tabular-nums">{fmt.money(totalMonthlyCommitment)}</p>
          </div>
        )}
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load your leases. Check your connection and try again.</p>}

        {status === 'ready' && leases.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={FileClock} title="No active leases" description="Lease an aircraft from the Fleet page to see it here." />
          </div>
        )}

        {status === 'ready' && leases.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Aircraft</TableHead>
                <TableHead>Monthly rate</TableHead>
                <TableHead>Started</TableHead>
                <TableHead>Next payment due</TableHead>
                <TableHead className="text-right">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {leases.map((lease) => (
                <TableRow key={lease.leaseId}>
                  <TableCell>
                    <p className="font-medium">{lease.registration}</p>
                    <p className="text-xs text-muted-foreground">{lease.aircraftTypeName}</p>
                  </TableCell>
                  <TableCell className="tabular-nums">{fmt.money(lease.monthlyRate)}</TableCell>
                  <TableCell className="text-sm text-muted-foreground">{formatDate(lease.startUtc)}</TableCell>
                  <TableCell className="tabular-nums">{formatDate(lease.nextPaymentUtc)}</TableCell>
                  <TableCell className="text-right">
                    <Button type="button" variant="outline" size="sm" onClick={() => onEndLease(lease)}>
                      <LogOut className="size-3.5" />
                      End lease
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}
