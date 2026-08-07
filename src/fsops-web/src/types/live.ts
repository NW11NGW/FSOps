export interface Heartbeat {
  serverTimeUtc: string
  simConnected: boolean
  version: string
}

export type HubStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
