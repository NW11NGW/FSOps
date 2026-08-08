import { useCallback, useEffect, useState } from 'react'

import { del, get, post } from '@/lib/api'
import type { HirePilotResponse, PilotSummary } from '@/types/pilot'

export type PilotsStatus = 'loading' | 'ready' | 'error'

interface UsePilotsResult {
  status: PilotsStatus
  pilots: PilotSummary[]
  refetch: () => void
  /** POST /pilots. Returns the new pilot and the airline's cash balance after the hiring cost;
   *  throws ApiError on failure so the caller can show the message. */
  hire: (name?: string) => Promise<HirePilotResponse>
  /** DELETE /pilots/{id}. Throws ApiError (400) if the pilot is currently flying. */
  release: (pilotId: string) => Promise<void>
}

/** The airline's virtual pilot roster (GET /pilots), refetchable after hire/release. */
export function usePilots(): UsePilotsResult {
  const [status, setStatus] = useState<PilotsStatus>('loading')
  const [pilots, setPilots] = useState<PilotSummary[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<PilotSummary[]>('/pilots')
      .then((result) => {
        if (cancelled) return
        setPilots(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  const hire = useCallback(
    async (name?: string) => {
      const result = await post<HirePilotResponse>('/pilots', { name })
      setPilots((current) => [...current, result.pilot])
      return result
    },
    [],
  )

  const release = useCallback(async (pilotId: string) => {
    await del<void>(`/pilots/${pilotId}`)
    setPilots((current) => current.filter((p) => p.id !== pilotId))
  }, [])

  return { status, pilots, refetch, hire, release }
}
