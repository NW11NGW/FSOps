import { useEffect, useState } from 'react'

import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ApiError, post } from '@/lib/api'
import type { RoutePreviewResponse } from '@/types/route'

export type FlightPreviewStatus = 'idle' | 'loading' | 'success' | 'error'

interface UseFlightPreviewResult {
  data: RoutePreviewResponse | null
  status: FlightPreviewStatus
  errorMessage: string | null
}

const DEBOUNCE_MS = 150

/**
 * Debounced POST /routes/preview scoped to one concrete aircraft type. A sibling of
 * useRoutePreview (used by the route planner, which previews against the fleet's default type)
 * rather than an extension of it - the Fly screen's brief always has a specific aircraft picked
 * and needs the preview to reflect that aircraft's performance.
 */
export function useFlightPreview(
  departureIcao: string | null,
  arrivalIcao: string | null,
  aircraftTypeId: string | null,
): UseFlightPreviewResult {
  const [data, setData] = useState<RoutePreviewResponse | null>(null)
  const [status, setStatus] = useState<FlightPreviewStatus>('idle')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const key = departureIcao && arrivalIcao ? `${departureIcao}|${arrivalIcao}|${aircraftTypeId ?? ''}` : null
  const debouncedKey = useDebouncedValue(key, DEBOUNCE_MS)

  useEffect(() => {
    if (!debouncedKey) {
      setData(null)
      setStatus('idle')
      setErrorMessage(null)
      return
    }

    const [dep, arr, typeId] = debouncedKey.split('|')
    const controller = new AbortController()
    setStatus('loading')
    setErrorMessage(null)

    post<RoutePreviewResponse>(
      '/routes/preview',
      { departureIcao: dep, arrivalIcao: arr, ...(typeId ? { aircraftTypeId: typeId } : {}) },
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
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not preview this flight.')
      })

    return () => controller.abort()
  }, [debouncedKey])

  return { data, status, errorMessage }
}
