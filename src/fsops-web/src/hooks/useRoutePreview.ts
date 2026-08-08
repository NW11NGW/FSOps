import { useEffect, useState } from 'react'

import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ApiError, post } from '@/lib/api'
import type { RoutePreviewResponse } from '@/types/route'

export type RoutePreviewStatus = 'idle' | 'loading' | 'success' | 'error'

interface UseRoutePreviewResult {
  data: RoutePreviewResponse | null
  status: RoutePreviewStatus
  /** True while a newer preview is loading but a previous one is still on screen - lets the UI
   *  keep the plan panel visible instead of flashing back to a skeleton on every keystroke. */
  isRefreshing: boolean
  errorMessage: string | null
}

const DEBOUNCE_MS = 300

/**
 * Debounced POST /routes/preview. The endpoint documents that it never throws in normal
 * operation (problems surface through `validation.warnings` instead), so an actual rejection
 * here means a real network/server failure - the previous preview is kept on screen and the
 * failure is surfaced separately rather than blanking the plan panel.
 *
 * `fareInBaseCurrency` (optional) re-runs the preview's live economics readout (expected
 * passengers, load factor, revenue per sector - see RouteEconomicsPreview) against that fare as
 * the user types it, same debounce as the airport pick itself.
 */
export function useRoutePreview(
  departureIcao: string | null,
  arrivalIcao: string | null,
  fareInBaseCurrency?: number | null,
): UseRoutePreviewResult {
  const [data, setData] = useState<RoutePreviewResponse | null>(null)
  const [status, setStatus] = useState<RoutePreviewStatus>('idle')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const debouncedDeparture = useDebouncedValue(departureIcao, DEBOUNCE_MS)
  const debouncedArrival = useDebouncedValue(arrivalIcao, DEBOUNCE_MS)
  const debouncedFare = useDebouncedValue(fareInBaseCurrency ?? null, DEBOUNCE_MS)

  useEffect(() => {
    if (!debouncedDeparture || !debouncedArrival) {
      setData(null)
      setStatus('idle')
      setErrorMessage(null)
      return
    }

    const controller = new AbortController()
    setStatus('loading')
    setErrorMessage(null)

    post<RoutePreviewResponse>(
      '/routes/preview',
      {
        departureIcao: debouncedDeparture,
        arrivalIcao: debouncedArrival,
        ...(debouncedFare !== null && debouncedFare > 0 ? { fare: debouncedFare } : {}),
      },
      undefined,
      { signal: controller.signal },
    )
      .then((result) => {
        setData(result)
        setStatus('success')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setStatus('error')
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not preview this route. Check your connection.')
      })

    return () => controller.abort()
  }, [debouncedDeparture, debouncedArrival, debouncedFare])

  return { data, status, isRefreshing: status === 'loading' && data !== null, errorMessage }
}
