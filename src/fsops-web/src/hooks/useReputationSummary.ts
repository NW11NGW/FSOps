import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { ReputationSummary } from '@/types/airline'

export type ReputationSummaryStatus = 'loading' | 'ready' | 'error' | 'none'

export interface ReputationSummaryState {
  status: ReputationSummaryStatus
  data: ReputationSummary | null
  refetch: () => void
}

/** Fetches GET /airline/reputation for the Dashboard's reputation card. 'none' mirrors the 204 the
 *  backend returns before an airline exists at all (distinct from 'error' - no airline yet is
 *  expected during onboarding, not a failure). */
export function useReputationSummary(): ReputationSummaryState {
  const [status, setStatus] = useState<ReputationSummaryStatus>('loading')
  const [data, setData] = useState<ReputationSummary | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<ReputationSummary | null>('/airline/reputation')
      .then((result) => {
        if (cancelled) return
        if (result === null || result === undefined) {
          setData(null)
          setStatus('none')
          return
        }
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
