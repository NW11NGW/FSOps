import { useEffect, useRef, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { VatsimTrafficResponse } from '@/types/operations'

/** Same interval discipline as useVatsimAtc - this polls our own server, never VATSIM directly,
 *  and the server's own cache/backoff (VatsimNetworkClient) governs how often the feed itself is
 *  actually asked. Slightly slower than the ATC poll: other pilots' traffic is presentation
 *  garnish for the map, not something that needs to feel as immediate as controller coverage. */
const POLL_INTERVAL_MS = 45_000
const ERROR_RETRY_MS = 15_000

export type VatsimTrafficFetchStatus = 'loading' | 'ready' | 'error'

interface VatsimTrafficState {
  status: VatsimTrafficFetchStatus
  data: VatsimTrafficResponse | null
}

/**
 * Polls GET /vatsim/traffic for the live operations map's "other people's traffic" layer (G11).
 * `enabled` is the client-side toggle (off by default on the dashboard map - see
 * vatsimTrafficVisibility): when false, this never polls at all, satisfying "never poll when the
 * feature is switched off" the same way the server itself never fetches VATSIM when nothing is
 * listening. Nothing else in the app reads this hook's data, so turning it off costs nothing
 * elsewhere - contrast VatsimHistoryCard, which polls its own separate endpoint.
 */
export function useVatsimTraffic(enabled: boolean): VatsimTrafficState {
  const [status, setStatus] = useState<VatsimTrafficFetchStatus>('loading')
  const [data, setData] = useState<VatsimTrafficResponse | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    if (!enabled) {
      setStatus('loading')
      setData(null)
      return undefined
    }

    let cancelled = false

    const poll = () => {
      get<VatsimTrafficResponse>('/vatsim/traffic')
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
  }, [enabled])

  return { status, data }
}
