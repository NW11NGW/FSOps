import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { FleetAircraftSummary } from '@/types/fleet'

export type FleetStatusState = 'loading' | 'ready' | 'error'

interface UseFleetResult {
  status: FleetStatusState
  fleet: FleetAircraftSummary[]
  refetch: () => void
}

/** The airline's fleet (GET /fleet), refetchable after buy/lease/a flight completing. */
export function useFleet(): UseFleetResult {
  const [status, setStatus] = useState<FleetStatusState>('loading')
  const [fleet, setFleet] = useState<FleetAircraftSummary[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<FleetAircraftSummary[]>('/fleet')
      .then((result) => {
        if (cancelled) return
        setFleet(result)
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

  return { status, fleet, refetch }
}
