import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { FinanceCosts, FinanceRoute, FinanceRoutesResponse } from '@/types/finance'
import type {
  StatsFleetAircraft,
  StatsFleetResponse,
  StatsPerformancePoint,
  StatsPerformanceResponse,
  StatsPilotLogbookEntry,
  StatsPilotsResponse,
  StatsTrendPoint,
  StatsTrendsResponse,
} from '@/types/stats'

export type StatsOverviewStatus = 'loading' | 'ready' | 'error'

interface UseStatsOverviewResult {
  status: StatsOverviewStatus
  periodDays: number
  setPeriodDays: (days: number) => void
  performance: StatsPerformancePoint[]
  /** One point per calendar day: cash, load factor, on-time, reputation - see StatsTrendPoint. */
  trends: StatsTrendPoint[]
  /** The airline's live reputation, for drawing today's standing against the pressure series. */
  currentReputation: number | null
  /** How many days in the window carry a genuinely recorded reputation score. */
  reputationRecordedDays: number
  fleet: StatsFleetAircraft[]
  pilots: StatsPilotLogbookEntry[]
  costs: FinanceCosts | null
  routes: FinanceRoute[]
  onlineSectorsFlown: number
  onlineEligibleSectorsFlown: number
  refetch: () => void
}

const DEFAULT_PERIOD_DAYS = 30

/**
 * Powers the whole Stats page in one round trip. `performance`/`fleet`/`pilots` come from the new
 * StatsEndpoints - every figure derived from posted Flight rows, never a cached total (see that
 * class's own doc). `costs`/`routes` are deliberately NOT a new computation: they reuse
 * FinanceEndpoints' own `/finance/costs` and `/finance/routes` directly, the same endpoints the
 * Finances page calls, so the revenue/cost split and per-route P&L can never disagree between the
 * two pages - there is exactly one place either figure is ever calculated.
 */
export function useStatsOverview(): UseStatsOverviewResult {
  const [status, setStatus] = useState<StatsOverviewStatus>('loading')
  const [periodDays, setPeriodDays] = useState(DEFAULT_PERIOD_DAYS)
  const [performance, setPerformance] = useState<StatsPerformancePoint[]>([])
  const [trends, setTrends] = useState<StatsTrendPoint[]>([])
  const [currentReputation, setCurrentReputation] = useState<number | null>(null)
  const [reputationRecordedDays, setReputationRecordedDays] = useState(0)
  const [fleet, setFleet] = useState<StatsFleetAircraft[]>([])
  const [pilots, setPilots] = useState<StatsPilotLogbookEntry[]>([])
  const [costs, setCosts] = useState<FinanceCosts | null>(null)
  const [routes, setRoutes] = useState<FinanceRoute[]>([])
  const [onlineSectorsFlown, setOnlineSectorsFlown] = useState(0)
  const [onlineEligibleSectorsFlown, setOnlineEligibleSectorsFlown] = useState(0)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    Promise.all([
      get<StatsPerformanceResponse>('/stats/performance', { days: periodDays }),
      get<StatsTrendsResponse>('/stats/trends', { days: periodDays }),
      get<StatsFleetResponse>('/stats/fleet', { days: periodDays }),
      get<StatsPilotsResponse>('/stats/pilots', { days: periodDays }),
      get<FinanceCosts>('/finance/costs', { days: periodDays }),
      get<FinanceRoutesResponse>('/finance/routes', { days: periodDays }),
    ])
      .then(([performanceResult, trendsResult, fleetResult, pilotsResult, costsResult, routesResult]) => {
        if (cancelled) return
        setPerformance(performanceResult.points)
        setTrends(trendsResult.points)
        setCurrentReputation(trendsResult.currentReputation)
        setReputationRecordedDays(trendsResult.reputationRecordedDays)
        setFleet(fleetResult.aircraft)
        setPilots(pilotsResult.pilots)
        setCosts(costsResult)
        setRoutes(routesResult.routes)
        setOnlineSectorsFlown(performanceResult.onlineSectorsFlown)
        setOnlineEligibleSectorsFlown(performanceResult.onlineEligibleSectorsFlown)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [periodDays, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return {
    status,
    periodDays,
    setPeriodDays,
    performance,
    trends,
    currentReputation,
    reputationRecordedDays,
    fleet,
    pilots,
    costs,
    routes,
    onlineSectorsFlown,
    onlineEligibleSectorsFlown,
    refetch,
  }
}
