import { useEffect, useRef, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { VatsimHistoryResponse } from '@/types/operations'

/** This is FSOps' own database, not a live feed - it only changes when a flight completes, so a
 *  slow poll is plenty. Kept as a poll rather than a one-shot fetch purely so a flight completing
 *  while the dashboard is open updates this card without a manual refresh. */
const POLL_INTERVAL_MS = 60_000
const ERROR_RETRY_MS = 20_000

export type VatsimHistoryFetchStatus = 'loading' | 'ready' | 'error'

interface VatsimHistoryState {
  status: VatsimHistoryFetchStatus
  data: VatsimHistoryResponse | null
}

/**
 * Polls GET /vatsim/history - G9's "which of my flights were flown online" card, built entirely
 * from FSOps' own records (see the endpoint's own doc for why this is never a second call to
 * VATSIM). Once a good response has been received, a later poll failing keeps showing the last
 * good data rather than flashing an error - same convention as useLiveOperations/useVatsimAtc.
 */
export function useVatsimHistory(): VatsimHistoryState {
  const [status, setStatus] = useState<VatsimHistoryFetchStatus>('loading')
  const [data, setData] = useState<VatsimHistoryResponse | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    let cancelled = false

    const poll = () => {
      get<VatsimHistoryResponse>('/vatsim/history')
        .then((result) => {
          if (cancelled) return
          setData(result)
          setStatus('ready')
          timerRef.current = setTimeout(poll, POLL_INTERVAL_MS)
        })
        .catch((err: unknown) => {
          if (cancelled) return
          if (!(err instanceof ApiError)) return
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
