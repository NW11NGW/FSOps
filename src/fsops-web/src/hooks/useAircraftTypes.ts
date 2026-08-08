import { useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { AircraftTypeOption } from '@/types/fleet'

export type AircraftTypesStatus = 'loading' | 'ready' | 'error'

interface UseAircraftTypesResult {
  status: AircraftTypesStatus
  types: AircraftTypeOption[]
}

/** The buy/lease catalogue (GET /fleet/aircraft-types) - loaded once, doesn't change during a session. */
export function useAircraftTypes(): UseAircraftTypesResult {
  const [status, setStatus] = useState<AircraftTypesStatus>('loading')
  const [types, setTypes] = useState<AircraftTypeOption[]>([])

  useEffect(() => {
    let cancelled = false

    get<AircraftTypeOption[]>('/fleet/aircraft-types')
      .then((result) => {
        if (cancelled) return
        setTypes(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [])

  return { status, types }
}
