import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { MaintenanceQuoteResponse } from '@/types/maintenance'

export type MaintenanceQuoteStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseMaintenanceQuoteResult {
  status: MaintenanceQuoteStatus
  quote: MaintenanceQuoteResponse | null
  /** Re-fetches without re-mounting the dialog - the world keeps moving while a confirmation
   *  dialog is open (a background virtual-pilot flight can ground or move the aircraft), so a
   *  dialog that gets a stale-quote refusal from the server should be able to re-quote in place. */
  refetch: () => void
}

/** GET /fleet/{id}/maintenance-quote - read-only, side-effect-free, safe to call on every render
 *  of a perform-maintenance-now dialog. `null` id reports 'idle' (dialog closed, nothing to
 *  preview). Same shape as useSaleQuote/useLeaseTerminationQuote. */
export function useMaintenanceQuote(fleetAircraftId: string | null): UseMaintenanceQuoteResult {
  const [status, setStatus] = useState<MaintenanceQuoteStatus>('idle')
  const [quote, setQuote] = useState<MaintenanceQuoteResponse | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!fleetAircraftId) {
      setStatus('idle')
      setQuote(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    get<MaintenanceQuoteResponse>(`/fleet/${fleetAircraftId}/maintenance-quote`)
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
