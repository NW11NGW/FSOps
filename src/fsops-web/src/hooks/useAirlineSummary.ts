import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { AirlineSummary } from '@/types/airline'

export type AirlineSummaryStatus = 'loading' | 'ready' | 'error'

export interface AirlineSummaryState {
  status: AirlineSummaryStatus
  data: AirlineSummary | null
  refetch: () => void
}

/** Fetches /airline/summary once (plus on demand via refetch) for the TopBar cash pill and Dashboard tiles. */
export function useAirlineSummary(): AirlineSummaryState {
  const [status, setStatus] = useState<AirlineSummaryStatus>('loading')
  const [data, setData] = useState<AirlineSummary | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<AirlineSummary>('/airline/summary')
      .then((result) => {
        if (cancelled) return
        setData(result)
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

  return { status, data, refetch }
}
