import { useEffect, useRef, useState } from 'react'

import { post } from '@/lib/api'
import type { RoutePreviewResponse, RouteSummary } from '@/types/route'

function routeKey(route: RouteSummary): string {
  return `${route.departureIcao}:${route.arrivalIcao}`
}

/**
 * GET /routes returns distance and fare but not block time, so this fills that column in by
 * calling the (non-throwing) preview endpoint per route against the airline's default fleet
 * aircraft. Results are cached by city pair so re-renders and refetches after create/delete
 * don't re-query routes that are already known.
 */
export function useRouteBlockTimes(routes: RouteSummary[]): Record<string, number | undefined> {
  const [blockMinutes, setBlockMinutes] = useState<Record<string, number | undefined>>({})
  const cacheRef = useRef<Map<string, number>>(new Map())

  useEffect(() => {
    let cancelled = false
    const pending = routes.filter((route) => !cacheRef.current.has(routeKey(route)))

    if (pending.length === 0) {
      setBlockMinutes(Object.fromEntries(routes.map((route) => [route.id, cacheRef.current.get(routeKey(route))])))
      return
    }

    Promise.all(
      pending.map((route) =>
        post<RoutePreviewResponse>('/routes/preview', {
          departureIcao: route.departureIcao,
          arrivalIcao: route.arrivalIcao,
        })
          .then((preview) => ({ route, minutes: preview.estimatedBlockMinutes as number | undefined }))
          .catch(() => ({ route, minutes: undefined as number | undefined })),
      ),
    ).then((results) => {
      if (cancelled) return
      for (const { route, minutes } of results) {
        if (minutes !== undefined) cacheRef.current.set(routeKey(route), minutes)
      }
      setBlockMinutes(Object.fromEntries(routes.map((route) => [route.id, cacheRef.current.get(routeKey(route))])))
    })

    return () => {
      cancelled = true
    }
  }, [routes])

  return blockMinutes
}
