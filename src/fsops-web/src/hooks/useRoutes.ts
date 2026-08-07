import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { RouteSummary } from '@/types/route'

export type RoutesStatus = 'loading' | 'ready' | 'error'

interface UseRoutesResult {
  status: RoutesStatus
  routes: RouteSummary[]
  refetch: () => void
}

/** The airline's saved routes (GET /routes), refetchable after create/delete. */
export function useRoutes(): UseRoutesResult {
  const [status, setStatus] = useState<RoutesStatus>('loading')
  const [routes, setRoutes] = useState<RouteSummary[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<RouteSummary[]>('/routes')
      .then((result) => {
        if (cancelled) return
        setRoutes(result)
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

  return { status, routes, refetch }
}
