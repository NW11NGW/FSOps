import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type { FleetLoan, LoanEligibility } from '@/types/fleet'

export type LoansStatus = 'loading' | 'ready' | 'error'

interface UseLoansResult {
  status: LoansStatus
  loans: FleetLoan[]
  eligibility: LoanEligibility | null
  refetch: () => void
}

/** The airline's loans plus current borrowing capacity (GET /fleet/loans, GET /fleet/loan-eligibility). */
export function useLoans(): UseLoansResult {
  const [status, setStatus] = useState<LoansStatus>('loading')
  const [loans, setLoans] = useState<FleetLoan[]>([])
  const [eligibility, setEligibility] = useState<LoanEligibility | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    Promise.all([get<FleetLoan[]>('/fleet/loans'), get<LoanEligibility>('/fleet/loan-eligibility')])
      .then(([loanResult, eligibilityResult]) => {
        if (cancelled) return
        setLoans(loanResult)
        setEligibility(eligibilityResult)
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

  return { status, loans, eligibility, refetch }
}
