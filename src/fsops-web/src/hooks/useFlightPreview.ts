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
 *
 * `fareInBaseCurrency` (optional) drives the live economics readout (expected passengers, load
 * factor, revenue per sector - see RouteEconomicsPreview) against the route's actual fare, since
 * the brief is pricing a real sector rather than letting the user try different fares.
 */
export function useFlightPreview(
  departureIcao: string | null,
  arrivalIcao: string | null,
  aircraftTypeId: string | null,
  fareInBaseCurrency?: number | null,
): UseFlightPreviewResult {
  const [data, setData] = useState<RoutePreviewResponse | null>(null)
  const [status, setStatus] = useState<FlightPreviewStatus>('idle')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const key = departureIcao && arrivalIcao
    ? `${departureIcao}|${arrivalIcao}|${aircraftTypeId ?? ''}|${fareInBaseCurrency ?? ''}`
    : null
  const debouncedKey = useDebouncedValue(key, DEBOUNCE_MS)

  useEffect(() => {
    if (!debouncedKey) {
      setData(null)
      setStatus('idle')
      setErrorMessage(null)
      return
    }

    const [dep, arr, typeId, fare] = debouncedKey.split('|')
    const parsedFare = Number(fare)
    const controller = new AbortController()
    setStatus('loading')
    setErrorMessage(null)

    post<RoutePreviewResponse>(
      '/routes/preview',
      {
        departureIcao: dep,
        arrivalIcao: arr,
        ...(typeId ? { aircraftTypeId: typeId } : {}),
        ...(fare && Number.isFinite(parsedFare) && parsedFare > 0 ? { fare: parsedFare } : {}),
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
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not preview this flight.')
      })

    return () => controller.abort()
  }, [debouncedKey])

  return { data, status, errorMessage }
}
