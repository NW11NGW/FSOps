import { useEffect, useRef, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { WorldDataStatus } from '@/types/airport'

const POLL_INTERVAL_MS = 2000
const ERROR_RETRY_MS = 5000

export type WorldDataFetchStatus = 'loading' | 'ready' | 'error'

interface WorldDataState {
  status: WorldDataFetchStatus
  data: WorldDataStatus | null
}

/**
 * Fetches world-data seed status on mount and keeps polling while an import
 * is in progress (first run seeds ~78,000 airports). Stops polling once the
 * import completes or the data is already seeded — the last successful
 * response is left in place as a silent refresh. The backend may not be up
 * yet, so fetch failures retry quietly instead of surfacing as a hard error.
 */
export function useWorldDataStatus(): WorldDataState {
  const [status, setStatus] = useState<WorldDataFetchStatus>('loading')
  const [data, setData] = useState<WorldDataStatus | null>(null)

  const timerRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    let cancelled = false

    const poll = () => {
      get<WorldDataStatus>('/worlddata/status')
        .then((result) => {
          if (cancelled) return
          setData(result)
          setStatus('ready')
          if (result.importInProgress) {
            timerRef.current = setTimeout(poll, POLL_INTERVAL_MS)
          }
        })
        .catch((err: unknown) => {
          if (cancelled) return
          if (!(err instanceof ApiError)) return // AbortError etc. — not applicable here
          setStatus('error')
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
