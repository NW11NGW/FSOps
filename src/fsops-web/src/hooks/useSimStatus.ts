import { useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { SimStatus } from '@/types/flight'

interface UseSimStatusResult {
  status: SimStatus | null
  loaded: boolean
}

const POLL_MS = 5000

/** Polls GET /sim/status while `enabled` - used for the pre-flight readiness checks (sim connection, loaded aircraft). */
export function useSimStatus(enabled: boolean): UseSimStatusResult {
  const [status, setStatus] = useState<SimStatus | null>(null)
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    if (!enabled) return undefined

    let cancelled = false

    const poll = () => {
      get<SimStatus>('/sim/status')
        .then((result) => {
          if (cancelled) return
          setStatus(result)
          setLoaded(true)
        })
        .catch(() => {
          if (cancelled) return
          setLoaded(true)
        })
    }

    poll()
    const id = setInterval(poll, POLL_MS)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [enabled])

  return { status, loaded }
}
