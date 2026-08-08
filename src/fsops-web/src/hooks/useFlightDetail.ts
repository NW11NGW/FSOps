import { useEffect, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { FlightDetail } from '@/types/flight'

export type FlightDetailStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseFlightDetailResult {
  status: FlightDetailStatus
  data: FlightDetail | null
  errorMessage: string | null
}

/** GET /flights/{id} - full detail (flight row + event history) for a report card, on demand. */
export function useFlightDetail(flightId: string | null): UseFlightDetailResult {
  const [status, setStatus] = useState<FlightDetailStatus>('idle')
  const [data, setData] = useState<FlightDetail | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  useEffect(() => {
    if (!flightId) {
      setStatus('idle')
      setData(null)
      setErrorMessage(null)
      return
    }

    let cancelled = false
    setStatus('loading')
    setErrorMessage(null)

    get<FlightDetail>(`/flights/${flightId}`)
      .then((result) => {
        if (cancelled) return
        setData(result)
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setStatus('error')
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not load this flight.')
      })

    return () => {
      cancelled = true
    }
  }, [flightId])

  return { status, data, errorMessage }
}
