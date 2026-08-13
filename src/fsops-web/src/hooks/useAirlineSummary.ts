import { useCallback, useEffect, useRef, useState } from 'react'

import { get } from '@/lib/api'
import type { AirlineSummary } from '@/types/airline'

export type AirlineSummaryStatus = 'loading' | 'ready' | 'error'

export interface AirlineSummaryState {
  status: AirlineSummaryStatus
  data: AirlineSummary | null
  refetch: () => void
}

/** How often the summary re-reads itself while the tab is visible. Virtual flights are resolved
 *  server-side on a 60-second wall clock, so this bounds how long the cash pill can disagree with
 *  the ledger. Deliberately not faster: the endpoint sums the whole ledger, and nobody watching a
 *  cash figure needs it to the second. */
const REFRESH_INTERVAL_MS = 30_000

/**
 * Fetches /airline/summary for the TopBar cash pill and the Dashboard tiles.
 *
 * It refreshes on an interval and whenever the window regains focus, not just on mount. That is
 * the fix for a real bug: a virtual pilot's flight lands on the server's wall clock and posts its
 * ledger transaction with nothing at all telling the SPA it happened. The cash pill therefore sat
 * on whatever it read when the page loaded, while the Finances page - which refetches every time
 * it mounts - showed the correct balance. Switching away from the ledger and back appeared to
 * "fix" it, because that remounted the page rather than because anything had synchronised.
 *
 * Polling rather than a push is deliberate for now. The SignalR hub carries flight telemetry only;
 * giving it an economy-changed event is the better long-term answer, but it would have to be
 * raised from every path that posts to the ledger - virtual resolution, maintenance, monthly
 * salaries, loan amortisation - and missing one would reintroduce exactly this bug in a form that
 * is harder to see. A cheap poll is correct regardless of which paths exist.
 */
export function useAirlineSummary(): AirlineSummaryState {
  const [status, setStatus] = useState<AirlineSummaryStatus>('loading')
  const [data, setData] = useState<AirlineSummary | null>(null)
  const [token, setToken] = useState(0)

  // Distinguishes the first load from a background refresh. Without it, every poll would flip the
  // status back to 'loading' and make the TopBar flicker through its skeleton twice a minute.
  const hasLoadedRef = useRef(false)

  useEffect(() => {
    let cancelled = false
    if (!hasLoadedRef.current) setStatus('loading')

    get<AirlineSummary>('/airline/summary')
      .then((result) => {
        if (cancelled) return
        hasLoadedRef.current = true
        setData(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        // A failed background refresh keeps the last known figure on screen rather than blanking
        // it - a momentarily stale number is far better than an error where the cash used to be.
        if (!hasLoadedRef.current) setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  useEffect(() => {
    // Nothing is watching a hidden tab, so don't spend requests on one; the visibility listener
    // below catches up the moment it comes back.
    const tick = () => {
      if (document.visibilityState === 'visible') refetch()
    }
    const interval = window.setInterval(tick, REFRESH_INTERVAL_MS)

    const onFocus = () => refetch()
    const onVisibility = () => {
      if (document.visibilityState === 'visible') refetch()
    }
    window.addEventListener('focus', onFocus)
    document.addEventListener('visibilitychange', onVisibility)

    return () => {
      window.clearInterval(interval)
      window.removeEventListener('focus', onFocus)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [refetch])

  return { status, data, refetch }
}
