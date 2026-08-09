import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type {
  FinanceCosts,
  FinanceLeasesResponse,
  FinanceLoan,
  FinancePilot,
  FinancePilotsResponse,
  FinanceRoute,
  FinanceRoutesResponse,
  FinanceSummary,
} from '@/types/finance'

export type FinanceOverviewStatus = 'loading' | 'ready' | 'error'

interface UseFinanceOverviewResult {
  status: FinanceOverviewStatus
  summary: FinanceSummary | null
  leases: FinanceLeasesResponse
  loans: FinanceLoan[]
  pilots: FinancePilot[]
  costs: FinanceCosts | null
  routes: FinanceRoute[]
  /** The window `costs`/`routes` were requested for - always show this next to those figures
   *  rather than assuming "30 days" once a selector exists. See types/finance.ts FinanceRoute doc. */
  periodDays: number
  refetch: () => void
}

const EMPTY_LEASES: FinanceLeasesResponse = { leases: [], totalMonthlyCommitment: 0 }
const DEFAULT_PERIOD_DAYS = 30

/**
 * Powers the whole Finances page in one round trip - summary, leases, loans, pilots, cost
 * breakdown and route P&L all load together (mirrors useLoans' Promise.all pattern), and any
 * mutation on the page (end a lease, settle/overpay a loan) calls the same `refetch` so nothing
 * on screen can go stale relative to what actually posted to the ledger.
 */
export function useFinanceOverview(periodDays: number = DEFAULT_PERIOD_DAYS): UseFinanceOverviewResult {
  const [status, setStatus] = useState<FinanceOverviewStatus>('loading')
  const [summary, setSummary] = useState<FinanceSummary | null>(null)
  const [leases, setLeases] = useState<FinanceLeasesResponse>(EMPTY_LEASES)
  const [loans, setLoans] = useState<FinanceLoan[]>([])
  const [pilots, setPilots] = useState<FinancePilot[]>([])
  const [costs, setCosts] = useState<FinanceCosts | null>(null)
  const [routes, setRoutes] = useState<FinanceRoute[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    Promise.all([
      get<FinanceSummary>('/finance/summary'),
      get<FinanceLeasesResponse>('/finance/leases'),
      get<{ loans: FinanceLoan[] }>('/finance/loans'),
      get<FinancePilotsResponse>('/finance/pilots', { days: periodDays }),
      get<FinanceCosts>('/finance/costs', { days: periodDays }),
      get<FinanceRoutesResponse>('/finance/routes', { days: periodDays }),
    ])
      .then(([summaryResult, leasesResult, loansResult, pilotsResult, costsResult, routesResult]) => {
        if (cancelled) return
        setSummary(summaryResult)
        setLeases(leasesResult)
        setLoans(loansResult.loans)
        setPilots(pilotsResult.pilots)
        setCosts(costsResult)
        setRoutes(routesResult.routes)
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

  return { status, summary, leases, loans, pilots, costs, routes, periodDays, refetch }
}
