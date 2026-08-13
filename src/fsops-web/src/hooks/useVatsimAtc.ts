import { useEffect, useRef, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { VatsimAtcResponse } from '@/types/operations'

/** Polls our own server, never VATSIM directly - the server caches/backs off against the feed on
 *  its own clock (see VatsimNetworkClient), so this interval only governs how often the dashboard
 *  asks *us* for the latest snapshot. Kept a little slower than useLiveOperations' aircraft poll:
 *  controllers logging on/off is a slow-moving signal compared to aircraft position. */
const POLL_INTERVAL_MS = 45_000
const ERROR_RETRY_MS = 15_000

export type VatsimAtcFetchStatus = 'loading' | 'ready' | 'error'

interface VatsimAtcState {
  status: VatsimAtcFetchStatus
  data: VatsimAtcResponse | null
}

export interface VatsimAtcOptions {
  /** `'all'` for every controller FSOps can place worldwide - what a map needs, because the user
   *  can pan anywhere and the list beside it has to agree with what is on screen. `'network'`
   *  (the default) returns only positions covering the airline's own airports, which is the right
   *  answer for a consumer with no viewport. */
  scope?: 'all' | 'network'
  /** Whether to ask for boundary polygons. Only a map should: the coordinates are tens of
   *  kilobytes a consumer that draws no map would parse and discard. */
  geometry?: boolean
}

/**
 * Polls GET /operations/atc for the dashboard's ATC layer. Once a good response has been
 * received, a later poll failing keeps showing the last good data instead of flashing an error
 * over a working map - only the very first load can surface 'error', same convention as
 * useLiveOperations.
 *
 * Panning and zooming the map does NOT re-run this. The viewport filter runs on the client over
 * data already held (see atcVisibility.ts), so the feed keeps its own cadence no matter how much
 * the user moves the map - which is both the etiquette rule and the reason the list can respond
 * instantly.
 */
export function useVatsimAtc(options: VatsimAtcOptions = {}): VatsimAtcState {
  const { scope = 'network', geometry = false } = options
  const [status, setStatus] = useState<VatsimAtcFetchStatus>('loading')
  const [data, setData] = useState<VatsimAtcResponse | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    let cancelled = false

    const query = new URLSearchParams()
    if (scope !== 'network') query.set('scope', scope)
    if (geometry) query.set('geometry', 'true')
    const path = query.size > 0 ? `/operations/atc?${query.toString()}` : '/operations/atc'

    const poll = () => {
      get<VatsimAtcResponse>(path)
        .then((result) => {
          if (cancelled) return
          setData(result)
          setStatus('ready')
          timerRef.current = setTimeout(poll, POLL_INTERVAL_MS)
        })
        .catch((err: unknown) => {
          if (cancelled) return
          if (!(err instanceof ApiError)) return // AbortError etc. - not applicable here
          setStatus((prev) => (prev === 'ready' ? 'ready' : 'error'))
          timerRef.current = setTimeout(poll, ERROR_RETRY_MS)
        })
    }

    poll()

    return () => {
      cancelled = true
      clearTimeout(timerRef.current)
    }
  }, [scope, geometry])

  return { status, data }
}
