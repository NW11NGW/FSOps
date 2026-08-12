import { useEffect, useRef, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { LiveOperationsResponse } from '@/types/operations'

/** Interpolated (virtual) traffic only needs to move about once a minute - its position is
 *  computed from the schedule and the clock, so polling faster buys nothing.
 *  The player's own aircraft, if any, is driven separately by
 *  useFlightLive's SignalR stream elsewhere in the app; this poll is for the dashboard overview. */
const POLL_INTERVAL_MS = 60_000
const ERROR_RETRY_MS = 15_000

export type LiveOperationsFetchStatus = 'loading' | 'ready' | 'error'

interface LiveOperationsState {
  status: LiveOperationsFetchStatus
  data: LiveOperationsResponse | null
}

/**
 * Polls GET /operations/live for the dashboard's live operations map. Once a good response has
 * been received, a later poll failing keeps showing the last good data instead of blanking the
 * map or flashing an error over working content - only the very first load can surface 'error'.
 */
export function useLiveOperations(): LiveOperationsState {
  const [status, setStatus] = useState<LiveOperationsFetchStatus>('loading')
  const [data, setData] = useState<LiveOperationsResponse | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    let cancelled = false

    const poll = () => {
      get<LiveOperationsResponse>('/operations/live')
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
