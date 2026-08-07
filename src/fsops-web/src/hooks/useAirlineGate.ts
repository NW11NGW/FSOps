import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { Airline } from '@/types/airline'

export type GateStatus = 'checking' | 'wizard' | 'app'

interface AirlineGateState {
  status: GateStatus
  airline: Airline | null
  markCreated: (airline: Airline) => void
}

/**
 * Decides whether the app boots into the full-screen onboarding wizard or
 * the normal shell, based on GET /airline (200 = airline exists, 204 = it
 * doesn't). If the backend can't be reached at all, we fail open into the
 * wizard rather than showing a broken dashboard.
 */
export function useAirlineGate(): AirlineGateState {
  const [status, setStatus] = useState<GateStatus>('checking')
  const [airline, setAirline] = useState<Airline | null>(null)

  useEffect(() => {
    let cancelled = false

    get<Airline | undefined>('/airline')
      .then((result) => {
        if (cancelled) return
        if (result) {
          setAirline(result)
          setStatus('app')
        } else {
          setStatus('wizard')
        }
      })
      .catch(() => {
        if (cancelled) return
        setStatus('wizard')
      })

    return () => {
      cancelled = true
    }
  }, [])

  const markCreated = useCallback((created: Airline) => {
    setAirline(created)
    setStatus('app')
  }, [])

  return { status, airline, markCreated }
}
