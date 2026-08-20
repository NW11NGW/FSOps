import { useCallback, useEffect, useRef, useState } from 'react'

import { get, post, put } from '@/lib/api'
import type { SimAircraftState, SimEdition } from '@/types/simAircraft'

export type SimAircraftStatus = 'loading' | 'ready' | 'error'

export interface UseSimAircraftResult {
  status: SimAircraftStatus
  state: SimAircraftState | null
  /** True while a scan or a save is in flight, so the whole card can be disabled as one. */
  busy: boolean
  refetch: () => void
  scan: () => Promise<void>
  setEdition: (edition: SimEdition) => Promise<void>
  setCommunityFolder: (path: string | null) => Promise<void>
  /** true ticks it on, false ticks it off, null clears the tick and lets FSOps decide again. */
  setAvailable: (typeDesignator: string, available: boolean | null) => Promise<void>
}

/**
 * The sim-aircraft settings card's data. Every mutation returns the whole new state from the
 * server rather than patching locally: availability is worked out from the edition, the scan and
 * the player's ticks together, so changing any one of them can change the answer for aircraft the
 * caller never mentioned. Guessing at that in the client is how the two drift apart.
 */
export function useSimAircraft(): UseSimAircraftResult {
  const [status, setStatus] = useState<SimAircraftStatus>('loading')
  const [state, setState] = useState<SimAircraftState | null>(null)
  const [busy, setBusy] = useState(false)
  const cancelledRef = useRef(false)

  const refetch = useCallback(() => {
    setStatus('loading')
    get<SimAircraftState>('/sim-aircraft')
      .then((result) => {
        if (cancelledRef.current) return
        setState(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelledRef.current) return
        setStatus('error')
      })
  }, [])

  useEffect(() => {
    cancelledRef.current = false
    refetch()
    return () => {
      cancelledRef.current = true
    }
  }, [refetch])

  const run = useCallback(async (work: () => Promise<SimAircraftState>) => {
    setBusy(true)
    try {
      const result = await work()
      if (!cancelledRef.current) {
        setState(result)
        setStatus('ready')
      }
    } finally {
      setBusy(false)
    }
  }, [])

  const scan = useCallback(
    () => run(() => post<SimAircraftState>('/sim-aircraft/scan')),
    [run],
  )

  const setEdition = useCallback(
    (edition: SimEdition) =>
      run(() => put<SimAircraftState>('/sim-aircraft', { edition, clearCommunityFolderPath: false })),
    [run],
  )

  const setCommunityFolder = useCallback(
    (path: string | null) =>
      run(() =>
        put<SimAircraftState>('/sim-aircraft', {
          // The edition has to go along for the ride: this endpoint stores the pair, and sending
          // the one already on screen is what keeps a folder change from resetting it.
          edition: state?.edition ?? 'Standard',
          communityFolderPath: path,
          clearCommunityFolderPath: path === null,
        }),
      ),
    [run, state?.edition],
  )

  const setAvailable = useCallback(
    (typeDesignator: string, available: boolean | null) =>
      run(() => put<SimAircraftState>(`/sim-aircraft/${encodeURIComponent(typeDesignator)}`, { available })),
    [run],
  )

  return { status, state, busy, refetch, scan, setEdition, setCommunityFolder, setAvailable }
}
