import { useEffect, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { FlightDetail } from '@/types/flight'

export type FlightDetailOnDemandStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseFlightDetailOnDemandResult {
  status: FlightDetailOnDemandStatus
  data: FlightDetail | null
  errorMessage: string | null
}

/**
 * Same GET /flights/{id} contract as useFlightDetail, fetched on demand rather than on mount:
 * the ledger opens a flight only when a row is clicked, so eagerly loading detail for every line
 * would be wasted work. Kept separate from the Fly page's own hook deliberately - the two have
 * different lifetimes and the Finances view should not take a dependency on Fly-page internals.
 * Backs the ledger's "view flight" drill-down (docs/PLAN.md "drillable to the flight that
 * produced a line") - reads types/flight.ts only.
 */
export function useFlightDetailOnDemand(flightId: string | null): UseFlightDetailOnDemandResult {
  const [status, setStatus] = useState<FlightDetailOnDemandStatus>('idle')
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
