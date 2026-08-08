import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { PlaystyleInfo } from '@/types/airline'

export type PlaystylesStatus = 'loading' | 'ready' | 'error'

export interface PlaystylesState {
  status: PlaystylesStatus
  playstyles: PlaystyleInfo[]
  refetch: () => void
}

/**
 * Fetches /airline/playstyles once - the starting capital, lease deposit term, starter lease rate
 * and monthly insurance behind each playstyle, sourced server-side from economy-config.json so the
 * onboarding wizard and Settings -> Airline never hand-write numbers that could drift from what
 * airline creation actually charges. Static reference data (no airline required), so this is safe
 * to call before an airline exists - same pattern as useStrategyProfiles.
 */
export function usePlaystyles(): PlaystylesState {
  const [status, setStatus] = useState<PlaystylesStatus>('loading')
  const [playstyles, setPlaystyles] = useState<PlaystyleInfo[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<PlaystyleInfo[]>('/airline/playstyles')
      .then((result) => {
        if (cancelled) return
        setPlaystyles(result)
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

  return { status, playstyles, refetch }
}
