import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { LedgerPage, LedgerTransactionEntry } from '@/types/finance'

export type LedgerStatus = 'loading' | 'ready' | 'error'

interface UseFinanceLedgerResult {
  status: LedgerStatus
  transactions: LedgerTransactionEntry[]
  total: number
  refetch: () => void
}

/** GET /finance/ledger?category=&skip=&take= - itemised, filterable, paginated. `category` of
 *  `null` omits the filter (every category). Refetches whenever the filter or page changes. */
export function useFinanceLedger(category: string | null, skip: number, take: number): UseFinanceLedgerResult {
  const [status, setStatus] = useState<LedgerStatus>('loading')
  const [transactions, setTransactions] = useState<LedgerTransactionEntry[]>([])
  const [total, setTotal] = useState(0)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<LedgerPage>('/finance/ledger', { category: category ?? undefined, skip, take })
      .then((result) => {
        if (cancelled) return
        setTransactions(result.transactions)
        setTotal(result.total)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [category, skip, take, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return { status, transactions, total, refetch }
}
