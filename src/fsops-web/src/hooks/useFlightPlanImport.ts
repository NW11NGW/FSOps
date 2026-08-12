import { useCallback, useEffect, useRef, useState } from 'react'

import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { get } from '@/lib/api'
import type { FlightPlanImport } from '@/types/flight'

export type FlightPlanImportStatus = 'idle' | 'loading' | 'success' | 'error'

interface UseFlightPlanImportResult {
  data: FlightPlanImport | null
  status: FlightPlanImportStatus
  /**
   * Re-runs the same GET immediately, bypassing the debounce - for the Fly screen's explicit
   * "Check for OFP" button. The auto-fetch below only re-fires when the route/aircraft selection
   * changes, so a plan the player just filed in SimBrief (in another tab, without touching the
   * Fly screen's selection) would otherwise never be picked up without a reselect. A no-op when
   * nothing is selected yet.
   */
  refresh: () => void
}

const DEBOUNCE_MS = 150

/**
 * GET /flights/plan-import - the SimBrief import hand-off. Fires automatically (debounced) on
 * every route/aircraft change, and again on demand via `refresh`. Purely informational and
 * read-only: resolves the same plan StartAsync would use for this route/aircraft pair, without
 * starting anything. `null` routeId means "nothing selected yet", not a failure - the Fly screen
 * has no route chosen at that point.
 */
export function useFlightPlanImport(routeId: string | null, fleetAircraftId: string | null): UseFlightPlanImportResult {
  const [data, setData] = useState<FlightPlanImport | null>(null)
  const [status, setStatus] = useState<FlightPlanImportStatus>('idle')
  // Tracks the in-flight request across both the auto-fetch effect and a manual refresh() so the
  // older of two overlapping requests can never clobber the newer one's result.
  const controllerRef = useRef<AbortController | null>(null)

  const key = routeId ? `${routeId}|${fleetAircraftId ?? ''}` : null
  const debouncedKey = useDebouncedValue(key, DEBOUNCE_MS)

  const runFetch = useCallback((route: string, fleetAircraft: string | null) => {
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    setStatus('loading')

    get<FlightPlanImport>(
      '/flights/plan-import',
      { routeId: route, ...(fleetAircraft ? { fleetAircraftId: fleetAircraft } : {}) },
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
  }, [])

  useEffect(() => {
    if (!debouncedKey) {
      controllerRef.current?.abort()
      setData(null)
      setStatus('idle')
      return
    }

    const [route, aircraft] = debouncedKey.split('|')
    if (!route) return
    runFetch(route, aircraft || null)

    return () => controllerRef.current?.abort()
  }, [debouncedKey, runFetch])

  const refresh = useCallback(() => {
    if (!routeId) return
    runFetch(routeId, fleetAircraftId ?? null)
  }, [routeId, fleetAircraftId, runFetch])

  return { data, status, refresh }
}
