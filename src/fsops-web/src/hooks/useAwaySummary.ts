import { useCallback, useEffect, useState } from 'react'

import { get, post } from '@/lib/api'
import type { AwaySummaryResponse } from '@/types/maintenance'

export type AwaySummaryStatus = 'loading' | 'ready' | 'error'

export interface UseAwaySummaryResult {
  status: AwaySummaryStatus
  summary: AwaySummaryResponse | null
  /** Marks the summary as seen - moves the server's cursor forward so the same catch-up isn't
   *  shown again next time the app opens. Clears the local summary immediately (optimistic - the
   *  dialog closing shouldn't wait on the round trip). */
  acknowledge: () => void
}

/**
 * Fetches GET /away-summary once on mount, so the app can explain catch-up rather than leaving a
 * returning player to reconstruct it from a transaction list.
 * `summary` is null (not an empty object) whenever there is genuinely nothing
 * to show (a normal reload, or a brand-new airline), so callers can gate rendering on
 * `summary !== null` rather than inspecting `hasSummary` themselves.
 */
export function useAwaySummary(): UseAwaySummaryResult {
  const [status, setStatus] = useState<AwaySummaryStatus>('loading')
  const [summary, setSummary] = useState<AwaySummaryResponse | null>(null)

  useEffect(() => {
    let cancelled = false

    get<AwaySummaryResponse>('/away-summary')
      .then((result) => {
        if (cancelled) return
        setSummary(result.hasSummary ? result : null)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
    // Intentionally runs once - this is a startup catch-up notice, not something that should
    // re-fetch on every re-render of whatever page mounts it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const acknowledge = useCallback(() => {
    setSummary(null)
    post('/away-summary/acknowledge').catch(() => {
      // Best-effort - if this fails the next GET simply reports the same window again, which is
      // safe (GET is side-effect-free); nothing here needs to block the dialog closing.
    })
  }, [])

  return { status, summary, acknowledge }
}
