import { useCallback, useEffect, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { ActiveFlightResponse } from '@/types/flight'

export type ActiveFlightStatus = 'loading' | 'none' | 'active' | 'error'

interface UseActiveFlightResult {
  status: ActiveFlightStatus
  data: ActiveFlightResponse | null
  errorMessage: string | null
  refetch: () => void
}

/**
 * GET /flights/active - the single source of truth for "is a flight in progress right now".
 * Called on mount (so a page refresh mid-flight rehydrates the live view instead of losing it)
 * and again via refetch() whenever the live hub reports a flight started/completed/needs
 * resolution.
 */
export function useActiveFlight(): UseActiveFlightResult {
  const [status, setStatus] = useState<ActiveFlightStatus>('loading')
  const [data, setData] = useState<ActiveFlightResponse | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')
    setErrorMessage(null)

    get<ActiveFlightResponse | undefined>('/flights/active')
      .then((result) => {
        if (cancelled) return
        if (result) {
          setData(result)
          setStatus('active')
        } else {
          setData(null)
          setStatus('none')
        }
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setStatus('error')
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not check for an active flight.')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return { status, data, errorMessage, refetch }
}
