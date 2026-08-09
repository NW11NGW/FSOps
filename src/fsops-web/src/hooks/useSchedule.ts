import { useCallback, useEffect, useState } from 'react'

import { get } from '@/lib/api'
import type {
  AircraftOptionsResponse,
  DayOfWeek,
  DutyDayInput,
  FleetAircraftLite,
  LegOptionsResponse,
  PilotSchedule,
  ScheduleConflictResponse,
  ScheduleDutyDay,
  ScheduleOverviewResponse,
} from '@/types/schedule'

export type ScheduleStatus = 'loading' | 'ready' | 'error'

export type SaveScheduleResult =
  | { ok: true; dutyDays: ScheduleDutyDay[]; autoSuspendOnMaintenance: boolean }
  | { ok: false; error: string; conflicts: string[] }

interface UseScheduleResult {
  status: ScheduleStatus
  dutyDays: ScheduleDutyDay[]
  /** Property of the whole schedule, not a day or leg - defaults to true (see types/schedule.ts). */
  autoSuspendOnMaintenance: boolean
  refetch: () => void
  /** PUT /pilots/{id}/schedule. Never throws - a 400 (conflicting schedule) resolves to
   *  `{ ok: false, error, conflicts }` so the caller can render every conflict sentence and keep
   *  the user's edits on screen instead of losing them to a thrown error.
   *  `autoSuspendOnMaintenance` is a required argument, not optional, deliberately - the backend
   *  resets it to `true` whenever it's omitted from the request body, so a caller that forgot to
   *  pass it would silently flip a player's "off" setting back on with their next save. */
  save: (dutyDays: DutyDayInput[], autoSuspendOnMaintenance: boolean) => Promise<SaveScheduleResult>
}

/**
 * A custom PUT (rather than lib/api.ts's `put()`) because the 400 response body carries a
 * `conflicts: string[]` array the shared error-extraction path discards - callers here need the
 * full list, not just a single summary message.
 */
async function putSchedule(pilotId: string, dutyDays: DutyDayInput[], autoSuspendOnMaintenance: boolean): Promise<SaveScheduleResult> {
  let response: Response
  try {
    response = await fetch(`/api/v1/pilots/${pilotId}/schedule`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ dutyDays, autoSuspendOnMaintenance }),
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
    const schedule = (payload ?? { pilotId, dutyDays: [], autoSuspendOnMaintenance: true }) as PilotSchedule
    return { ok: true, dutyDays: schedule.dutyDays, autoSuspendOnMaintenance: schedule.autoSuspendOnMaintenance }
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
  const [dutyDays, setDutyDays] = useState<ScheduleDutyDay[]>([])
  const [autoSuspendOnMaintenance, setAutoSuspendOnMaintenance] = useState(true)
  const [token, setToken] = useState(0)

  useEffect(() => {
    if (!pilotId) {
      setDutyDays([])
      setAutoSuspendOnMaintenance(true)
      setStatus('loading')
      return
    }

    let cancelled = false
    setStatus('loading')

    get<PilotSchedule>(`/pilots/${pilotId}/schedule`)
      .then((result) => {
        if (cancelled) return
        setDutyDays(result.dutyDays)
        setAutoSuspendOnMaintenance(result.autoSuspendOnMaintenance)
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
    async (nextDutyDays: DutyDayInput[], nextAutoSuspendOnMaintenance: boolean) => {
      if (!pilotId) return { ok: false as const, error: 'No pilot selected.', conflicts: [] }
      const result = await putSchedule(pilotId, nextDutyDays, nextAutoSuspendOnMaintenance)
      if (result.ok) {
        setDutyDays(result.dutyDays)
        setAutoSuspendOnMaintenance(result.autoSuspendOnMaintenance)
      }
      return result
    },
    [pilotId],
  )

  return { status, dutyDays, autoSuspendOnMaintenance, refetch, save }
}

/**
 * POST /pilots/{id}/schedule/aircraft-options - step one of the picker (docs/PLAN.md "2b"): "pick
 * the aircraft first". Not a subscription, called on demand when the player opens a duty day that
 * doesn't have an aircraft chosen yet (or wants to change it).
 */
export function fetchAircraftOptions(pilotId: string, day: DayOfWeek): Promise<AircraftOptionsResponse> {
  return fetch(`/api/v1/pilots/${pilotId}/schedule/aircraft-options`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ day }),
  }).then(async (response) => {
    if (!response.ok) throw new Error('Could not load aircraft for this day.')
    return (await response.json()) as AircraftOptionsResponse
  })
}

/**
 * POST /pilots/{id}/schedule/leg-options - step two: with the aircraft already fixed, which
 * routes can it fly at this day/time. `draftDutyDays` is the pilot's current on-screen draft
 * (saved or not) so the backend judges the candidate against what's actually been built so far -
 * this is what makes it possible to build a week up one leg at a time (week-closure is only
 * checked at save).
 */
export function fetchLegOptions(
  pilotId: string,
  day: DayOfWeek,
  time: string,
  fleetAircraftId: string,
  draftDutyDays?: DutyDayInput[] | null,
): Promise<LegOptionsResponse> {
  return fetch(`/api/v1/pilots/${pilotId}/schedule/leg-options`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({
      day,
      time,
      fleetAircraftId,
      draftDutyDays: draftDutyDays && draftDutyDays.length > 0 ? draftDutyDays : null,
    }),
  }).then(async (response) => {
    if (!response.ok) throw new Error('Could not load legs for this slot.')
    return (await response.json()) as LegOptionsResponse
  })
}

export type ScheduleOverviewStatus = 'loading' | 'ready' | 'error'

interface UseScheduleOverviewResult {
  status: ScheduleOverviewStatus
  overview: ScheduleOverviewResponse | null
  refetch: () => void
}

/** GET /pilots/schedule/overview - the read-only, airline-wide "is my fleet actually being used"
 *  view (docs/PLAN.md "2a"). */
export function useScheduleOverview(): UseScheduleOverviewResult {
  const [status, setStatus] = useState<ScheduleOverviewStatus>('loading')
  const [overview, setOverview] = useState<ScheduleOverviewResponse | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')

    get<ScheduleOverviewResponse>('/pilots/schedule/overview')
      .then((result) => {
        if (cancelled) return
        setOverview(result)
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

  return { status, overview, refetch }
}

export type FleetLiteStatus = 'loading' | 'ready' | 'error'

interface UseFleetLiteResult {
  status: FleetLiteStatus
  fleet: FleetAircraftLite[]
  refetch: () => void
}

/** GET /fleet, typed to only the fields the schedule builder needs (see FleetAircraftLite) - used
 *  by the fleet-availability panel to flag A-check risk on aircraft this schedule uses. */
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
