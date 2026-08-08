import { useEffect, useRef, useState } from 'react'

import { get } from '@/lib/api'
import type { AirportSummary } from '@/types/airport'

/**
 * Fetches and caches AirportSummary (coordinates included) for a set of ICAO codes via
 * GET /airports/{icao}, one call per unique code not already known. Backs the route map's
 * "spider web" view - saved routes only carry ICAO codes, not coordinates, so their airports'
 * lat/lon have to be resolved before a great-circle arc or marker can be drawn for them.
 * Cached by ref (same pattern as useRouteBlockTimes) so re-renders and refetches after
 * create/delete don't re-request airports that are already known.
 */
export function useAirportCoordinates(icaos: string[]): Record<string, AirportSummary> {
  const [byIcao, setByIcao] = useState<Record<string, AirportSummary>>({})
  const cacheRef = useRef<Map<string, AirportSummary>>(new Map())

  useEffect(() => {
    let cancelled = false
    const unique = Array.from(new Set(icaos.filter(Boolean)))
    const pending = unique.filter((icao) => !cacheRef.current.has(icao))

    if (pending.length === 0) {
      if (unique.length > 0) {
        setByIcao(Object.fromEntries(unique.map((icao) => [icao, cacheRef.current.get(icao)!])))
      }
      return
    }

    Promise.all(
      pending.map((icao) =>
        get<AirportSummary>(`/airports/${icao}`)
          .then((airport) => ({ icao, airport }))
          .catch(() => ({ icao, airport: undefined as AirportSummary | undefined })),
      ),
    ).then((results) => {
      if (cancelled) return
      for (const { icao, airport } of results) {
        if (airport) cacheRef.current.set(icao, airport)
      }
      setByIcao(
        Object.fromEntries(
          unique.filter((icao) => cacheRef.current.has(icao)).map((icao) => [icao, cacheRef.current.get(icao)!]),
        ),
      )
    })

    return () => {
      cancelled = true
    }
  }, [icaos])

  return byIcao
}
