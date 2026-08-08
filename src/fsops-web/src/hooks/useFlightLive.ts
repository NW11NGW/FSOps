import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'

import type { HubStatus } from '@/types/live'
import type { LiveFlightSnapshot, TelemetryPayload } from '@/types/flight'

interface UseFlightLiveOptions {
  onFlightCompleted?: (flightId: string) => void
  onFlightNeedsResolution?: (flightId: string) => void
}

interface FlightLiveState {
  hubStatus: HubStatus
  telemetry: TelemetryPayload | null
  flightUpdate: LiveFlightSnapshot | null
}

const RETRY_DELAY_MS = 5000

/**
 * A dedicated /hubs/live connection for the Fly screen's telemetry/flightUpdate/flightCompleted/
 * flightNeedsResolution events. Kept separate from useLiveConnection's heartbeat-only connection
 * (used by the app shell) rather than extending it, so this feature doesn't touch shared
 * connection-management code other pages depend on.
 */
export function useFlightLive(options: UseFlightLiveOptions = {}): FlightLiveState {
  const [hubStatus, setHubStatus] = useState<HubStatus>('connecting')
  const [telemetry, setTelemetry] = useState<TelemetryPayload | null>(null)
  const [flightUpdate, setFlightUpdate] = useState<LiveFlightSnapshot | null>(null)

  const onCompletedRef = useRef(options.onFlightCompleted)
  const onNeedsResolutionRef = useRef(options.onFlightNeedsResolution)
  onCompletedRef.current = options.onFlightCompleted
  onNeedsResolutionRef.current = options.onFlightNeedsResolution

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 15000])
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    connection.on('telemetry', (payload: TelemetryPayload) => {
      if (!cancelled) setTelemetry(payload)
    })
    connection.on('flightUpdate', (payload: LiveFlightSnapshot) => {
      if (!cancelled) setFlightUpdate(payload)
    })
    connection.on('flightCompleted', (payload: { flightId: string }) => {
      onCompletedRef.current?.(payload.flightId)
    })
    connection.on('flightNeedsResolution', (payload: { flightId: string }) => {
      onNeedsResolutionRef.current?.(payload.flightId)
    })
    // Server broadcasts (Clients.All) reach every connection regardless of which events it cares
    // about - this connection doesn't need heartbeats (useLiveConnection owns that), but without a
    // handler SignalR logs a "no client method" warning for every one that arrives.
    connection.on('heartbeat', () => {})

    connection.onreconnecting(() => {
      if (!cancelled) setHubStatus('reconnecting')
    })
    connection.onreconnected(() => {
      if (!cancelled) setHubStatus('connected')
    })
    connection.onclose(() => {
      if (!cancelled) setHubStatus('disconnected')
    })

    const attemptStart = () => {
      if (cancelled) return
      setHubStatus('connecting')
      connection
        .start()
        .then(() => {
          if (!cancelled) setHubStatus('connected')
        })
        .catch(() => {
          if (cancelled) return
          setHubStatus('disconnected')
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

  return { hubStatus, telemetry, flightUpdate }
}
