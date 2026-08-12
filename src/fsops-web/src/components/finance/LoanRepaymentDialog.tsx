import { useEffect, useState } from 'react'
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
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useLoanSettlementQuote } from '@/hooks/useLoanSettlementQuote'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import type { FinanceLoan, LoanOverpayResult, LoanSettleResult } from '@/types/finance'

interface LoanRepaymentDialogProps {
  loan: FinanceLoan | null
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

type Mode = 'full' | 'partial'

/**
 * Loans, with partial and full repayment: pay off in full (showing the payoff
 * figure and interest saved before confirming) or overpay any amount against the principal, always
 * blocked before it would drive cash negative, always stated consistently as shortening the term
 * rather than lowering the monthly payment.
 */
export function LoanRepaymentDialog({ loan, onOpenChange, onSuccess }: LoanRepaymentDialogProps) {
  const { fmt } = useSettings()
  const [mode, setMode] = useState<Mode>('full')
  const [amount, setAmount] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [staleQuoteNotice, setStaleQuoteNotice] = useState<string | null>(null)

  const { status: quoteStatus, quote, reload: reloadQuote } = useLoanSettlementQuote(
    mode === 'full' ? (loan?.loanId ?? null) : null,
  )

  // Keyed on the loan's id, not the loan object: callers build the prop as a fresh object literal
  // on every render, and the Finances page re-renders on the cash-balance heartbeat - depending on
  // the object identity would reset this dialog roughly once a second and wipe whatever the player
  // was typing.
  useEffect(() => {
    setMode('full')
    setAmount('')
    setError(null)
    setStaleQuoteNotice(null)
  }, [loan?.loanId])

  const parsedAmount = Number(amount)
  const remainingBalance = loan?.remainingBalance ?? 0
  const partialValid = parsedAmount > 0 && parsedAmount <= remainingBalance
  const previewNewBalance = partialValid ? remainingBalance - parsedAmount : remainingBalance

  async function handlePayFull() {
    if (!loan || !quote) return
    setSubmitting(true)
    setError(null)
    setStaleQuoteNotice(null)
    try {
      // The figure the player is looking at travels with the request. Settlement is irreversible,
      // so the server recomputes and refuses rather than charging an amount they never agreed to -
      // a background billing tick can move the balance between opening this dialog and confirming.
      const result = await post<LoanSettleResult>(`/finance/loans/${loan.loanId}/settle`, {
        expectedTotalPayoff: quote.totalPayoff,
      })
      toast.success(`Loan settled for ${fmt.money(result.totalPayoff)}. Cash balance now ${fmt.money(result.cashBalance)}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      if (err instanceof ApiError) {
        // A refusal here is the guard working, not a failure - re-quote and let them decide again.
        setStaleQuoteNotice(err.message)
        reloadQuote()
      } else {
        setError('Could not settle this loan. Check your connection and try again.')
      }
    } finally {
      setSubmitting(false)
    }
  }

  async function handleOverpay() {
    if (!loan || !partialValid) return
    setSubmitting(true)
    setError(null)
    try {
      const result = await post<LoanOverpayResult>(`/finance/loans/${loan.loanId}/overpay`, { amount: parsedAmount })
      toast.success(`Overpaid ${fmt.money(result.amountApplied)}. Balance now ${fmt.money(result.newRemainingBalance)}.`)
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not apply this overpayment. Check your connection and try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={loan !== null} onOpenChange={(next) => !submitting && onOpenChange(next)}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Repay loan</DialogTitle>
          <DialogDescription>
            Balance {loan ? fmt.money(loan.remainingBalance) : ''} at {loan?.annualInterestRate.toFixed(2)}% APR
          </DialogDescription>
        </DialogHeader>

        <Tabs value={mode} onValueChange={(v) => setMode(v as Mode)}>
          <TabsList>
            <TabsTrigger value="full">Pay off in full</TabsTrigger>
            <TabsTrigger value="partial">Partial overpayment</TabsTrigger>
          </TabsList>
        </Tabs>

        {mode === 'full' && (
          <div className="space-y-3">
            {staleQuoteNotice && (
              <div className="rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning">
                <p className="font-medium">This loan&rsquo;s figures changed</p>
                <p className="mt-1">{staleQuoteNotice}</p>
                <p className="mt-1">Nothing has been paid. Check the updated payoff below and confirm again if you still want to settle.</p>
              </div>
            )}
            {quoteStatus === 'loading' && <Skeleton className="h-24 w-full" />}
            {quoteStatus === 'error' && <p className="text-sm text-danger">Could not price this settlement. Check your connection and try again.</p>}
            {quoteStatus === 'ready' && quote && (
              <div className="space-y-2 rounded-md border border-border p-3 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Remaining balance</span>
                  <span className="tabular-nums">{fmt.money(quote.remainingBalance)}</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Early-settlement fee</span>
                  <span className="tabular-nums">{fmt.money(quote.earlySettlementFee)}</span>
                </div>
                <div className="flex items-center justify-between border-t border-border pt-2 text-base">
                  <span className="font-medium">Total payoff</span>
                  <span className="font-semibold tabular-nums">{fmt.money(quote.totalPayoff)}</span>
                </div>
                <div className="flex items-center justify-between text-xs text-success">
                  <span>Interest saved</span>
                  <span className="tabular-nums">{fmt.money(quote.interestSaved)}</span>
                </div>
              </div>
            )}
          </div>
        )}

        {mode === 'partial' && (
          <div className="space-y-3">
            <div className="space-y-1.5">
              <Label htmlFor="overpay-amount">Amount to overpay</Label>
              <Input
                id="overpay-amount"
                type="number"
                min={0}
                max={remainingBalance}
                value={amount}
                onChange={(e) => setAmount(e.target.value)}
                placeholder={remainingBalance.toFixed(2)}
              />
              <p className="text-xs text-muted-foreground">Up to the remaining balance of {fmt.money(remainingBalance)}. No fee on a partial overpayment.</p>
            </div>

            {partialValid && (
              <div className="space-y-2 rounded-md border border-border p-3 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">New remaining balance</span>
                  <span className="font-medium tabular-nums">{fmt.money(previewNewBalance)}</span>
                </div>
                <p className="text-xs text-muted-foreground">
                  This shortens how long the loan runs - your monthly payment stays the same and the loan finishes sooner.
                </p>
              </div>
            )}

            {amount.trim() !== '' && !partialValid && (
              <p className="text-sm text-danger">
                {parsedAmount > remainingBalance
                  ? 'This exceeds the remaining balance - use "Pay off in full" instead.'
                  : 'Enter an amount greater than zero.'}
              </p>
            )}
          </div>
        )}

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          {mode === 'full' ? (
            <Button onClick={handlePayFull} disabled={submitting || quoteStatus !== 'ready' || !quote}>
              {submitting ? 'Settling…' : quote ? `Pay off - ${fmt.money(quote.totalPayoff)}` : 'Pay off in full'}
            </Button>
          ) : (
            <Button onClick={handleOverpay} disabled={submitting || !partialValid}>
              {submitting ? 'Applying…' : 'Overpay'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
