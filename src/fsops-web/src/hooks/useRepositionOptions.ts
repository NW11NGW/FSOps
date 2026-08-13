import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { RepositionOptions } from '@/types/fleet'

export type RepositionOptionsStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseRepositionOptionsResult {
  status: RepositionOptionsStatus
  options: RepositionOptions | null
  /** Re-fetches without re-mounting the dialog - used to re-quote after the server refuses a move
   *  because the fee moved between quote and commit (see RepositionAircraftDialog). */
  refetch: () => void
}

/** GET /fleet/{id}/reposition-options - read-only and side-effect-free, safe to call on every open
 *  of the reposition dialog. A `null` id reports 'idle' (dialog closed, nothing to preview), the
 *  same convention as useSaleQuote/useLeaseTerminationQuote. */
export function useRepositionOptions(fleetAircraftId: string | null): UseRepositionOptionsResult {
  const [status, setStatus] = useState<RepositionOptionsStatus>('idle')
  const [options, setOptions] = useState<RepositionOptions | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!fleetAircraftId) {
      setStatus('idle')
      setOptions(null)
      return
    }

    let cancelled = false
    setStatus('loading')

    get<RepositionOptions>(`/fleet/${fleetAircraftId}/reposition-options`)
      .then((result) => {
        if (cancelled) return
        setOptions(result)
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

  return { status, options, refetch }
}
