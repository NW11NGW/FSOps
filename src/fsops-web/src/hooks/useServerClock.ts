import { useEffect, useRef, useState } from 'react'

import type { Heartbeat } from '@/types/live'

/**
 * Ticks a local clock kept in sync with the server's UTC time. Each heartbeat
 * updates the client/server offset; between heartbeats we keep ticking off
 * the client clock plus that offset, so the display stays smooth at 1s
 * resolution instead of jumping only when a heartbeat arrives.
 */
export function useServerClock(heartbeat: Heartbeat | null): Date | null {
  const offsetRef = useRef<number | null>(null)
  const [now, setNow] = useState<Date | null>(null)

  useEffect(() => {
    if (!heartbeat) return
    const serverMs = Date.parse(heartbeat.serverTimeUtc)
    if (Number.isNaN(serverMs)) return
    offsetRef.current = serverMs - Date.now()
  }, [heartbeat])

  useEffect(() => {
    const tick = () => {
      if (offsetRef.current !== null) {
        setNow(new Date(Date.now() + offsetRef.current))
      }
    }
    tick()
    const id = setInterval(tick, 1000)
    return () => clearInterval(id)
  }, [])

  return now
}
