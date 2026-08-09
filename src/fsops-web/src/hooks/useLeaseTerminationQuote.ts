import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { LeaseTerminationQuote } from '@/types/fleet'

export type LeaseTerminationQuoteStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseLeaseTerminationQuoteResult {
  status: LeaseTerminationQuoteStatus
  quote: LeaseTerminationQuote | null
  /** Re-fetches without re-mounting the dialog - used to re-quote after the server refuses an
   *  end-lease because the figures moved between quote and commit (see EndLeaseDialog). */
  refetch: () => void
}

/** GET /fleet/{id}/lease-termination-quote - the pro-rata amount, early-termination fee and total
 *  charge, shown before confirming an early lease return. `null` id reports 'idle'. */
export function useLeaseTerminationQuote(fleetAircraftId: string | null): UseLeaseTerminationQuoteResult {
  const [status, setStatus] = useState<LeaseTerminationQuoteStatus>('idle')
  const [quote, setQuote] = useState<LeaseTerminationQuote | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!fleetAircraftId) {
      setStatus('idle')
      setQuote(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    get<LeaseTerminationQuote>(`/fleet/${fleetAircraftId}/lease-termination-quote`)
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
  }, [fleetAircraftId, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return { status, quote, refetch }
}
