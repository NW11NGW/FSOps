import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { StrategyProfileInfo } from '@/types/airline'

export type StrategyProfilesStatus = 'loading' | 'ready' | 'error'

export interface StrategyProfilesState {
  status: StrategyProfilesStatus
  profiles: StrategyProfileInfo[]
  refetch: () => void
}

/**
 * Fetches /airline/strategy-profiles once - the per-strategy fare/demand/cost figures and
 * advisory rules, sourced server-side from economy-config.json and RoutePreviewCalculator so the
 * onboarding wizard and Settings -> Airline never hand-write numbers that could drift from the
 * real simulation. Static reference data (no airline required), so this is safe to call before
 * an airline exists.
 */
export function useStrategyProfiles(): StrategyProfilesState {
  const [status, setStatus] = useState<StrategyProfilesStatus>('loading')
  const [profiles, setProfiles] = useState<StrategyProfileInfo[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<StrategyProfileInfo[]>('/airline/strategy-profiles')
      .then((result) => {
        if (cancelled) return
        setProfiles(result)
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

  return { status, profiles, refetch }
}
