import { useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { FlightTrack } from '@/types/flight'

export type FlightTrackStatus = 'idle' | 'loading' | 'ready' | 'error'

interface UseFlightTrackResult {
  status: FlightTrackStatus
  track: FlightTrack | null
}

/**
 * GET /flights/{id}/track - the path a flight actually flew.
 *
 * Fetched on demand, only once a flight's track is actually being shown, because a long-haul
 * sector's snapshot stream is far larger than the rest of a flight's record put together. Passing
 * null leaves the hook idle and makes no request at all.
 *
 * An empty `points` array is a normal, successful answer - older flights predate position
 * snapshots, and every virtual-pilot flight had no simulator attached and recorded none. Callers
 * must distinguish that from `status === 'error'`, which means the request itself failed.
 */
export function useFlightTrack(flightId: string | null): UseFlightTrackResult {
  const [status, setStatus] = useState<FlightTrackStatus>('idle')
  const [track, setTrack] = useState<FlightTrack | null>(null)

  useEffect(() => {
    if (!flightId) {
      setStatus('idle')
      setTrack(null)
      return
    }

    let cancelled = false
    setStatus('loading')
    setTrack(null)

    get<FlightTrack>(`/flights/${flightId}/track`)
      .then((result) => {
        if (cancelled) return
        setTrack(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [flightId])

  return { status, track }
}
