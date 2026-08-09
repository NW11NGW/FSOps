import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { SaleQuote } from '@/types/fleet'

export type SaleQuoteStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseSaleQuoteResult {
  status: SaleQuoteStatus
  quote: SaleQuote | null
  /** Re-fetches without re-mounting the dialog - used to re-quote after the server refuses a
   *  sell because the figures moved between quote and commit (see SellAircraftDialog). */
  refetch: () => void
}

/** GET /fleet/{id}/sale-quote - read-only, side-effect-free, safe to call on every render of a
 *  sell-confirmation dialog. `null` id reports 'idle' (dialog closed, nothing to preview). */
export function useSaleQuote(fleetAircraftId: string | null): UseSaleQuoteResult {
  const [status, setStatus] = useState<SaleQuoteStatus>('idle')
  const [quote, setQuote] = useState<SaleQuote | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!fleetAircraftId) {
      setStatus('idle')
      setQuote(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    get<SaleQuote>(`/fleet/${fleetAircraftId}/sale-quote`)
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
