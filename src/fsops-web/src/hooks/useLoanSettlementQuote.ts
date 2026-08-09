import { useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { LoanSettlementQuote } from '@/types/finance'

export type LoanSettlementQuoteStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseLoanSettlementQuoteResult {
  status: LoanSettlementQuoteStatus
  quote: LoanSettlementQuote | null
  /** Re-fetch the quote. Needed because settlement is guarded against a stale figure: if the
   *  payoff moves between quoting and confirming, the server refuses and the dialog has to show
   *  the player the new number rather than an error. */
  reload: () => void
}

/** GET /finance/loans/{id}/settlement-quote - the payoff figure and interest saved, shown before
 *  confirming a full early settlement. `null` id reports 'idle' (nothing to preview). */
export function useLoanSettlementQuote(loanId: string | null): UseLoanSettlementQuoteResult {
  const [status, setStatus] = useState<LoanSettlementQuoteStatus>('idle')
  const [quote, setQuote] = useState<LoanSettlementQuote | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)

  useEffect(() => {
    if (!loanId) {
      setStatus('idle')
      setQuote(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    get<LoanSettlementQuote>(`/finance/loans/${loanId}/settlement-quote`)
      .then((result) => {
        if (cancelled) return
        setQuote(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [loanId, refreshKey])

  return { status, quote, reload: () => setRefreshKey((key) => key + 1) }
}
