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

/**
 * Polls GET /operations/atc for the dashboard's ATC layer. Once a good response has been
 * received, a later poll failing keeps showing the last good data instead of flashing an error
 * over a working map - only the very first load can surface 'error', same convention as
 * useLiveOperations.
 */
export function useVatsimAtc(): VatsimAtcState {
  const [status, setStatus] = useState<VatsimAtcFetchStatus>('loading')
  const [data, setData] = useState<VatsimAtcResponse | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    let cancelled = false

    const poll = () => {
      get<VatsimAtcResponse>('/operations/atc')
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
  }, [])

  return { status, data }
}
