import { useEffect, useState } from 'react'

import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { get } from '@/lib/api'
import type { FlightPlanImport } from '@/types/flight'

export type FlightPlanImportStatus = 'idle' | 'loading' | 'success' | 'error'

interface UseFlightPlanImportResult {
  data: FlightPlanImport | null
  status: FlightPlanImportStatus
}

const DEBOUNCE_MS = 150

/**
 * Debounced GET /flights/plan-import - the SimBrief import hand-off.
 * Purely informational and read-only: resolves the same plan StartAsync would use for
 * this route/aircraft pair, without starting anything. `null` routeId means "nothing selected
 * yet", not a failure - the Fly screen has no route chosen at that point.
 */
export function useFlightPlanImport(routeId: string | null, fleetAircraftId: string | null): UseFlightPlanImportResult {
  const [data, setData] = useState<FlightPlanImport | null>(null)
  const [status, setStatus] = useState<FlightPlanImportStatus>('idle')

  const key = routeId ? `${routeId}|${fleetAircraftId ?? ''}` : null
  const debouncedKey = useDebouncedValue(key, DEBOUNCE_MS)

  useEffect(() => {
    if (!debouncedKey) {
      setData(null)
      setStatus('idle')
      return
    }

    const [route, aircraft] = debouncedKey.split('|')
    const controller = new AbortController()
    setStatus('loading')

    get<FlightPlanImport>(
      '/flights/plan-import',
      { routeId: route, ...(aircraft ? { fleetAircraftId: aircraft } : {}) },
      { signal: controller.signal },
    )
      .then((result) => {
        setData(result)
        setStatus('success')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setData(null)
        setStatus('error')
      })

    return () => controller.abort()
  }, [debouncedKey])

  return { data, status }
}
