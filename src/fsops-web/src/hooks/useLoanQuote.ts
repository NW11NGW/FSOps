import { useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { LoanQuote } from '@/types/fleet'

export type LoanQuoteStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseLoanQuoteResult {
  status: LoanQuoteStatus
  quote: LoanQuote | null
}

/**
 * Live, server-computed preview for a specific amount/term (GET /fleet/loan-quote) - the rate,
 * monthly repayment and total interest a loan of exactly this shape would actually carry if taken
 * right now. See docs/PLAN.md "Show the rate before the player commits" - the rate is always
 * computed server-side (LoanRateCalculator), never guessed client-side, so this can never disagree
 * with what POST /fleet/loans actually charges.
 *
 * Debounced so editing the amount/term inputs doesn't fire a request per keystroke. Reports
 * 'idle' (no quote, nothing shown) whenever the inputs don't yet describe a valid loan, so callers
 * never render a stale quote against invalid amount/term.
 */
export function useLoanQuote(amount: number, termMonths: number): UseLoanQuoteResult {
  const [status, setStatus] = useState<LoanQuoteStatus>('idle')
  const [quote, setQuote] = useState<LoanQuote | null>(null)

  useEffect(() => {
    if (!(amount > 0) || !(termMonths >= 1 && termMonths <= 360)) {
      setStatus('idle')
      setQuote(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    const handle = setTimeout(() => {
      get<LoanQuote>('/fleet/loan-quote', { amount, termMonths })
        .then((result) => {
          if (cancelled) return
          setQuote(result)
          setStatus('ready')
        })
        .catch(() => {
          if (cancelled) return
          setQuote(null)
          setStatus('error')
        })
    }, 300)

    return () => {
      cancelled = true
      clearTimeout(handle)
    }
  }, [amount, termMonths])

  return { status, quote }
}
