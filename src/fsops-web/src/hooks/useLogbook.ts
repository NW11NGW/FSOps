import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { LogbookResponse, LogbookSector } from '@/types/flight'

export type LogbookStatus = 'loading' | 'ready' | 'error'

interface UseLogbookResult {
  status: LogbookStatus
  sectors: LogbookSector[]
  /** Every sector the airline has ever flown, which may be more than `sectors.length`. */
  totalSectors: number
  refetch: () => void
}

/**
 * GET /flights/logbook - every sector actually flown, newest first, already joined to its route,
 * aircraft, pilot and posted ledger lines server-side.
 *
 * One request for the whole logbook rather than a page per scroll: sorting and filtering happen in
 * the browser, over a list the server has already capped, so changing the sort never costs a round
 * trip. `totalSectors` is the honest total so the UI can say when it is showing a slice.
 */
export function useLogbook(): UseLogbookResult {
  const [status, setStatus] = useState<LogbookStatus>('loading')
  const [sectors, setSectors] = useState<LogbookSector[]>([])
  const [totalSectors, setTotalSectors] = useState(0)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<LogbookResponse>('/flights/logbook')
      .then((result) => {
        if (cancelled) return
        setSectors(result.sectors)
        setTotalSectors(result.totalSectors)
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

  return { status, sectors, totalSectors, refetch }
}
