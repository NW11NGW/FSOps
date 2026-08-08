import { useCallback, useEffect, useState } from 'react'

import { get, post } from '@/lib/api'
import type {
  DayOfWeek,
  FleetAircraftLite,
  PilotSchedule,
  ScheduleConflictResponse,
  ScheduleEntry,
  ScheduleEntryInput,
  ScheduleOptionsRequest,
  ScheduleOptionsResponse,
} from '@/types/schedule'

export type ScheduleStatus = 'loading' | 'ready' | 'error'

export type SaveScheduleResult =
  | { ok: true; entries: ScheduleEntry[] }
  | { ok: false; error: string; conflicts: string[] }

interface UseScheduleResult {
  status: ScheduleStatus
  entries: ScheduleEntry[]
  refetch: () => void
  /** PUT /pilots/{id}/schedule. Never throws - a 400 (conflicting schedule) resolves to
   *  `{ ok: false, error, conflicts }` so the caller can render every conflict sentence and keep
   *  the user's edits on screen instead of losing them to a thrown error. */
  save: (entries: ScheduleEntryInput[]) => Promise<SaveScheduleResult>
}

/**
 * A custom PUT (rather than lib/api.ts's `put()`) because the 400 response body carries a
 * `conflicts: string[]` array the shared error-extraction path discards - callers here need the
 * full list, not just a single summary message.
 */
async function putSchedule(pilotId: string, entries: ScheduleEntryInput[]): Promise<SaveScheduleResult> {
  let response: Response
  try {
    response = await fetch(`/api/v1/pilots/${pilotId}/schedule`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ entries }),
    })
  } catch {
    return { ok: false, error: 'Could not reach the server. Check your connection and try again.', conflicts: [] }
  }

  let payload: unknown = null
  try {
    payload = await response.json()
  } catch {
    payload = null
  }

  if (response.ok) {
    const schedule = (payload ?? { pilotId, entries: [] }) as PilotSchedule
    return { ok: true, entries: schedule.entries }
  }

  const record = (payload && typeof payload === 'object' ? payload : {}) as Partial<ScheduleConflictResponse>
  return {
    ok: false,
    error: record.error ?? response.statusText ?? 'Could not save this schedule.',
    conflicts: Array.isArray(record.conflicts) ? record.conflicts : [],
  }
}

/** A pilot's standing weekly schedule (GET/PUT /pilots/{id}/schedule). Pass `null` while no pilot
 *  is selected - the hook stays idle rather than issuing a request. */
export function useSchedule(pilotId: string | null): UseScheduleResult {
  const [status, setStatus] = useState<ScheduleStatus>('loading')
  const [entries, setEntries] = useState<ScheduleEntry[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!pilotId) {
      setEntries([])
      setStatus('loading')
      return
    }

    let cancelled = false
    setStatus('loading')

    get<PilotSchedule>(`/pilots/${pilotId}/schedule`)
      .then((result) => {
        if (cancelled) return
        setEntries(result.entries)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [pilotId, token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  const save = useCallback(
    async (nextEntries: ScheduleEntryInput[]) => {
      if (!pilotId) return { ok: false as const, error: 'No pilot selected.', conflicts: [] }
      const result = await putSchedule(pilotId, nextEntries)
      if (result.ok) setEntries(result.entries)
      return result
    },
    [pilotId],
  )

  return { status, entries, refetch, save }
}

/**
 * POST /pilots/{id}/schedule/options - on-demand (not a subscription), called when the user is
 * choosing a day/time slot to add or move a leg into. `time` is "HH:mm". `draftEntries` is the
 * pilot's current on-screen draft (saved or not) so the backend judges the candidate against what
 * has actually been built so far, not just what's persisted - this is what makes it possible to
 * build a week up one leg at a time (week-closure is only checked at save).
 */
export function fetchScheduleOptions(
  pilotId: string,
  day: DayOfWeek,
  time: string,
  draftEntries?: ScheduleEntryInput[] | null,
): Promise<ScheduleOptionsResponse> {
  const body: ScheduleOptionsRequest = { day, time, draftEntries: draftEntries && draftEntries.length > 0 ? draftEntries : null }
  return post<ScheduleOptionsResponse>(`/pilots/${pilotId}/schedule/options`, body)
}

export type FleetLiteStatus = 'loading' | 'ready' | 'error'

interface UseFleetLiteResult {
  status: FleetLiteStatus
  fleet: FleetAircraftLite[]
  refetch: () => void
}

/** GET /fleet, typed to only the fields the schedule builder needs (see FleetAircraftLite) - used
 *  to show which aircraft an entry uses and to flag the reserved-for-player / grounded ones. */
export function useFleetLite(): UseFleetLiteResult {
  const [status, setStatus] = useState<FleetLiteStatus>('loading')
  const [fleet, setFleet] = useState<FleetAircraftLite[]>([])
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<FleetAircraftLite[]>('/fleet')
      .then((result) => {
        if (cancelled) return
        setFleet(result)
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

  return { status, fleet, refetch }
}
