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
 */
export function useRoutePreview(departureIcao: string | null, arrivalIcao: string | null): UseRoutePreviewResult {
  const [data, setData] = useState<RoutePreviewResponse | null>(null)
  const [status, setStatus] = useState<RoutePreviewStatus>('idle')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const debouncedDeparture = useDebouncedValue(departureIcao, DEBOUNCE_MS)
  const debouncedArrival = useDebouncedValue(arrivalIcao, DEBOUNCE_MS)

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
      { departureIcao: debouncedDeparture, arrivalIcao: debouncedArrival },
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
  }, [debouncedDeparture, debouncedArrival])

  return { data, status, isRefreshing: status === 'loading' && data !== null, errorMessage }
}
