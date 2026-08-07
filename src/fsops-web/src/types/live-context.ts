import type { Heartbeat, HubStatus } from '@/types/live'

export interface LiveContext {
  status: HubStatus
  heartbeat: Heartbeat | null
}
