import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { FleetAdviceResponse, OpportunitiesResponse, RoutePricingResponse } from '@/types/planning'

export type PlanningStatus = 'idle' | 'loading' | 'ready' | 'error'

interface PlanningQuery<T> {
  data: T | null
  status: PlanningStatus
  /** True while a newer answer is loading with a previous one still on screen - lets a panel stay
   *  visible instead of flashing back to a skeleton every time an input moves. */
  isRefreshing: boolean
  refetch: () => void
}

/**
 * GET /routes/{id}/pricing - the fare workbench for one saved route. Passing `null` for the route
 * id parks the hook in `idle` (nothing selected), so a dialog can mount before a route is chosen.
 *
 * The whole answer, curve included, is recomputed server-side for whatever fare is passed, because
 * the fare-independent half of the sector (block time, fuel, the market that day) is shared across
 * every point - which is what makes the curve's shape attributable to the fare and to nothing else.
 */
export function useRoutePricing(routeId: string | null, fareInBaseCurrency: number | null): PlanningQuery<RoutePricingResponse> {
  const [data, setData] = useState<RoutePricingResponse | null>(null)
  const [status, setStatus] = useState<PlanningStatus>('idle')
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!routeId) {
      setData(null)
      setStatus('idle')
      return
    }

    const controller = new AbortController()
    setStatus('loading')

    get<RoutePricingResponse>(
      `/routes/${routeId}/pricing`,
      { fare: fareInBaseCurrency !== null && fareInBaseCurrency > 0 ? fareInBaseCurrency : undefined },
      { signal: controller.signal },
    )
      .then((result) => {
        setData(result)
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setStatus('error')
      })

    return () => controller.abort()
  }, [routeId, fareInBaseCurrency, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])
  return { data, status, isRefreshing: status === 'loading' && data !== null, refetch }
}

/** GET /planning/opportunities - ranked city pairs worth opening. */
export function useOpportunities(enabled: boolean): PlanningQuery<OpportunitiesResponse> {
  const [data, setData] = useState<OpportunitiesResponse | null>(null)
  const [status, setStatus] = useState<PlanningStatus>('idle')
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!enabled) {
      setStatus('idle')
      return
    }

    const controller = new AbortController()
    setStatus('loading')

    get<OpportunitiesResponse>('/planning/opportunities', undefined, { signal: controller.signal })
      .then((result) => {
        setData(result)
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setStatus('error')
      })

    return () => controller.abort()
  }, [enabled, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])
  return { data, status, isRefreshing: status === 'loading' && data !== null, refetch }
}

/** GET /planning/fleet-advice - utilisation, gaps, and what acquiring an aircraft would change. */
export function useFleetAdvice(enabled: boolean): PlanningQuery<FleetAdviceResponse> {
  const [data, setData] = useState<FleetAdviceResponse | null>(null)
  const [status, setStatus] = useState<PlanningStatus>('idle')
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!enabled) {
      setStatus('idle')
      return
    }

    const controller = new AbortController()
    setStatus('loading')

    get<FleetAdviceResponse>('/planning/fleet-advice', undefined, { signal: controller.signal })
      .then((result) => {
        setData(result)
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setStatus('error')
      })

    return () => controller.abort()
  }, [enabled, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])
  return { data, status, isRefreshing: status === 'loading' && data !== null, refetch }
}
