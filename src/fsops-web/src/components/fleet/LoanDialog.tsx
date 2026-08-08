import { useState } from 'react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useLoans } from '@/hooks/useLoans'
import { useLoanQuote } from '@/hooks/useLoanQuote'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'

interface LoanDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

export function LoanDialog({ open, onOpenChange, onSuccess }: LoanDialogProps) {
  const { eligibility, loans, status } = useLoans()
  const { fmt } = useSettings()

  const [amount, setAmount] = useState('')
  const [termMonths, setTermMonths] = useState('60')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const parsedAmount = Number(amount)
  const parsedTerm = Number(termMonths)

  // The rate, monthly repayment and total interest are ALWAYS server-computed - see
  // docs/PLAN.md "Loan interest is set by the simulation, never by the player". There is no
  // client-side rate input or preview formula to keep in sync: this dialog shows exactly what
  // POST /fleet/loans will charge if submitted unchanged.
  const { status: quoteStatus, quote } = useLoanQuote(parsedAmount, parsedTerm)

  const inputsValid = parsedAmount > 0 && parsedTerm >= 1 && parsedTerm <= 360
  const overLimit = quoteStatus === 'ready' && quote !== null && !quote.isEligible
  const canSubmit = inputsValid && quoteStatus === 'ready' && quote !== null && quote.isEligible && !submitting

  async function handleSubmit() {
    setSubmitting(true)
    setError(null)
    try {
      await post('/fleet/loans', { amount: parsedAmount, termMonths: parsedTerm })
      toast.success(`Loan of ${fmt.money(parsedAmount)} taken out.`)
      setAmount('')
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not take out this loan. Check your connection and try again.')
    } finally {
      setSubmitting(false)
    }
  }

  const outstandingLoans = loans.filter((l) => !l.isPaidOff)

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!submitting) onOpenChange(next)
      }}
    >
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Take out a loan</DialogTitle>
          <DialogDescription>
            Borrow against your airline to acquire an aircraft sooner. The rate is priced automatically from how much
            of your borrowing capacity this loan would use - it is never something you choose.
          </DialogDescription>
        </DialogHeader>

        <div className="rounded-md border border-border bg-muted/40 p-3 text-sm">
          {status === 'loading' && <p className="text-muted-foreground">Checking your borrowing capacity…</p>}
          {status === 'ready' && eligibility && (
            <>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Trailing 30-day cash flow</span>
                <span className="font-medium tabular-nums">{fmt.money(eligibility.trailing30DayNetOperatingCashFlow)}</span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-muted-foreground">Max new monthly payment</span>
                <span className="font-medium tabular-nums">{fmt.money(eligibility.maxMonthlyPayment)}</span>
              </div>
              <p className="mt-2 text-xs text-muted-foreground">
                Capped at {Math.round(eligibility.maxDebtServiceFraction * 100)}% of your recent trading cash flow, so borrowing
                grows with your airline rather than trivialising it.
              </p>
            </>
          )}
          {status === 'error' && <p className="text-danger">Could not check your borrowing capacity.</p>}
        </div>

        {outstandingLoans.length > 0 && (
          <div className="space-y-1 text-xs text-muted-foreground">
            <p className="font-medium text-foreground">Existing loans</p>
            {outstandingLoans.map((loan) => (
              <div key={loan.id} className="flex items-center justify-between">
                <span>
                  Balance {fmt.money(loan.remainingBalance)} at {loan.annualInterestRate.toFixed(2)}% APR
                </span>
                <span>{fmt.money(loan.monthlyPayment)}/mo</span>
              </div>
            ))}
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="loan-amount">Amount</Label>
            <Input id="loan-amount" type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} placeholder="500,000" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="loan-term">Term (months)</Label>
            <Input id="loan-term" type="number" min={1} max={360} value={termMonths} onChange={(e) => setTermMonths(e.target.value)} />
          </div>
        </div>

        {inputsValid && quoteStatus === 'loading' && (
          <p className="text-sm text-muted-foreground">Pricing this loan…</p>
        )}

        {inputsValid && quoteStatus === 'ready' && quote && (
          <div className="space-y-2 rounded-md border border-border p-3 text-sm">
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground">Annual rate</span>
              <span className="font-medium tabular-nums">{quote.annualRatePct.toFixed(2)}%</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground">Monthly repayment</span>
              <span className={overLimit ? 'font-medium text-danger' : 'font-medium tabular-nums'}>{fmt.money(quote.monthlyPayment)}</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground">Total interest over the term</span>
              <span className="font-medium tabular-nums">{fmt.money(quote.totalInterest)}</span>
            </div>
            <p className="text-xs text-muted-foreground">
              Priced from how much of your borrowing capacity this loan uses - a smaller amount or a longer term lowers the
              rate.
            </p>
          </div>
        )}

        {inputsValid && quoteStatus === 'error' && (
          <p className="text-sm text-danger">Could not price this loan. Check your connection and try again.</p>
        )}

        {overLimit && (
          <p className="text-sm text-danger">
            This exceeds what your recent cash flow can service. Try a smaller amount, a longer term, or grow your revenue first.
          </p>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={!canSubmit}>
            {submitting ? 'Working…' : 'Take out loan'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
