import { Landmark } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useSettings } from '@/hooks/useSettings'
import { cn } from '@/lib/utils'
import type { FinanceLoan } from '@/types/finance'

interface LoansSectionProps {
  status: 'loading' | 'ready' | 'error'
  loans: FinanceLoan[]
  onRepay: (loan: FinanceLoan) => void
}

/**
 * Loans, with partial and full repayment: original amount, outstanding principal,
 * rate, monthly repayment, remaining term and total interest still to pay for every loan. Paid-off
 * loans are shown (so the player can see they're no longer being charged) but visually
 * de-emphasised rather than hidden.
 */
export function LoansSection({ status, loans, onRepay }: LoansSectionProps) {
  const { fmt } = useSettings()

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <Landmark className="size-4 text-muted-foreground" />
          Loans
        </CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        {status === 'loading' && (
          <div className="space-y-2 p-6 pt-0">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        )}

        {status === 'error' && <p className="px-6 pb-6 text-sm text-danger">Could not load your loans. Check your connection and try again.</p>}

        {status === 'ready' && loans.length === 0 && (
          <div className="px-6 pb-6">
            <EmptyState icon={Landmark} title="No loans" description="Take out a loan from the Fleet page to acquire an aircraft sooner." />
          </div>
        )}

        {status === 'ready' && loans.length > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Principal</TableHead>
                <TableHead>Balance</TableHead>
                <TableHead>Rate</TableHead>
                <TableHead>Monthly payment</TableHead>
                <TableHead>Remaining term</TableHead>
                <TableHead>Interest remaining</TableHead>
                <TableHead className="text-right">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loans.map((loan) => (
                <TableRow key={loan.loanId} className={cn(loan.isPaidOff && 'opacity-60')}>
                  <TableCell className="tabular-nums">{fmt.money(loan.principal)}</TableCell>
                  <TableCell className="tabular-nums">
                    {fmt.money(loan.remainingBalance)}
                    {loan.isPaidOff && (
                      <Badge variant="muted" className="ml-2">
                        Paid off
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell className="tabular-nums">{loan.annualInterestRate.toFixed(2)}%</TableCell>
                  <TableCell className="tabular-nums">{loan.isPaidOff ? '—' : fmt.money(loan.monthlyPayment)}</TableCell>
                  <TableCell className="tabular-nums">
                    {loan.isPaidOff ? '—' : `${loan.remainingTermMonths} of ${loan.termMonths} mo`}
                  </TableCell>
                  <TableCell className="tabular-nums">{loan.isPaidOff ? '—' : fmt.money(loan.totalInterestRemaining)}</TableCell>
                  <TableCell className="text-right">
                    {!loan.isPaidOff && (
                      <Button type="button" variant="outline" size="sm" onClick={() => onRepay(loan)}>
                        Repay
                      </Button>
                    )}
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
