import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { Flight } from '@/types/flight'

export type FlightHistoryStatus = 'loading' | 'ready' | 'error'

interface UseFlightHistoryResult {
  status: FlightHistoryStatus
  flights: Flight[]
  refetch: () => void
}

/** GET /flights - every flight the airline has ever started, most recent first. */
export function useFlightHistory(): UseFlightHistoryResult {
  const [status, setStatus] = useState<FlightHistoryStatus>('loading')
  const [flights, setFlights] = useState<Flight[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<Flight[]>('/flights')
      .then((result) => {
        if (cancelled) return
        setFlights(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return { status, flights, refetch }
}
