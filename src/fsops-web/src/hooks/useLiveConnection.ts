import { useEffect, useState } from 'react'
import * as signalR from '@microsoft/signalr'

import type { Heartbeat, HubStatus } from '@/types/live'

interface LiveConnectionState {
  status: HubStatus
  heartbeat: Heartbeat | null
}

const RETRY_DELAY_MS = 5000

/**
 * Connects to the live hub and tracks server heartbeats. The backend may not
 * be up yet (e.g. during early frontend dev) — a failed start() is expected
 * and just leaves the pill on "disconnected" while we keep retrying quietly.
 */
export function useLiveConnection(): LiveConnectionState {
  const [status, setStatus] = useState<HubStatus>('connecting')
  const [heartbeat, setHeartbeat] = useState<Heartbeat | null>(null)

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 15000])
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    connection.on('heartbeat', (payload: Heartbeat) => {
      if (!cancelled) setHeartbeat(payload)
    })

    connection.onreconnecting(() => {
      if (!cancelled) setStatus('reconnecting')
    })

    connection.onreconnected(() => {
      if (!cancelled) setStatus('connected')
    })

    connection.onclose(() => {
      if (!cancelled) setStatus('disconnected')
    })

    const attemptStart = () => {
      if (cancelled) return
      setStatus('connecting')
      connection
        .start()
        .then(() => {
          if (!cancelled) setStatus('connected')
        })
        .catch(() => {
          if (cancelled) return
          setStatus('disconnected')
          retryTimer = setTimeout(attemptStart, RETRY_DELAY_MS)
        })
    }

    attemptStart()

    return () => {
      cancelled = true
      if (retryTimer) clearTimeout(retryTimer)
      connection.stop().catch(() => {})
    }
  }, [])

  return { status, heartbeat }
}
