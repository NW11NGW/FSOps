import type { AirlineSummaryState } from '@/hooks/useAirlineSummary'
import type { Heartbeat, HubStatus } from '@/types/live'

export interface LiveContext {
  status: HubStatus
  heartbeat: Heartbeat | null
  airlineSummary: AirlineSummaryState
}
