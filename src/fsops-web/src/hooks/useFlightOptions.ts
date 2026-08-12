import { useCallback, useEffect, useState } from 'react'

import { ApiError, get } from '@/lib/api'
import type { FlightOption, FlightOptionAircraft } from '@/types/flight'

export type FlightOptionsStatus = 'loading' | 'ready' | 'unavailable' | 'error'

interface UseFlightOptionsResult {
  status: FlightOptionsStatus
  options: FlightOption[]
  errorMessage: string | null
  refetch: () => void
}

type Raw = Record<string, unknown>

function str(raw: Raw, key: string): string | null {
  const value = raw[key]
  return typeof value === 'string' && value.length > 0 ? value : null
}

function num(raw: Raw, key: string): number | null {
  const value = raw[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function bool(raw: Raw, key: string, fallback: boolean): boolean {
  const value = raw[key]
  return typeof value === 'boolean' ? value : fallback
}

function normalizeAircraftOption(raw: unknown): FlightOptionAircraft | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Raw
  const fleetAircraftId = str(r, 'fleetAircraftId')
  if (!fleetAircraftId) return null
  return {
    fleetAircraftId,
    registration: str(r, 'registration') ?? '',
    aircraftTypeId: str(r, 'aircraftTypeId'),
    aircraftTypeName: str(r, 'aircraftTypeName') ?? '',
    paxCapacity: num(r, 'paxCapacity'),
    estimatedBlockMinutes: num(r, 'estimatedBlockMinutes'),
    isFlyable: bool(r, 'isFlyable', false),
    reason: str(r, 'reason'),
  }
}

/**
 * GET /flights/options' settled DTO shape: every fleet
 * aircraft physically at a route's departure airport appears in `aircraftOptions`, flyable or
 * not, each with its own `isFlyable`/`reason` - never a single implicit "the" aircraft for a
 * route. `reason` at the route level is only set when `aircraftOptions` is empty entirely.
 */
function normalizeOption(raw: unknown): FlightOption | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Raw
  const routeId = str(r, 'routeId')
  const departureIcao = str(r, 'departureIcao')
  const arrivalIcao = str(r, 'arrivalIcao')
  if (!routeId || !departureIcao || !arrivalIcao) return null

  const rawAircraftOptions = r.aircraftOptions
  const aircraftOptions = Array.isArray(rawAircraftOptions)
    ? rawAircraftOptions.map(normalizeAircraftOption).filter((a): a is FlightOptionAircraft => a !== null)
    : []

  return {
    routeId,
    flightNumber: str(r, 'flightNumber'),
    departureIcao,
    departureName: str(r, 'departureName'),
    arrivalIcao,
    arrivalName: str(r, 'arrivalName'),
    distanceNm: num(r, 'distanceNm') ?? 0,
    estimatedBlockMinutes: num(r, 'estimatedBlockMinutes'),
    isFlyable: bool(r, 'isFlyable', aircraftOptions.some((a) => a.isFlyable)),
    reason: str(r, 'reason'),
    aircraftOptions,
  }
}

/**
 * GET /flights/options - the flyable-routes list for the Fly screen. Treated as an optional,
 * still-arriving capability: a 404 (endpoint not deployed) reports as 'unavailable' rather than
 * 'error' so the page can degrade to a simpler route list instead of showing a scary error.
 */
export function useFlightOptions(): UseFlightOptionsResult {
  const [status, setStatus] = useState<FlightOptionsStatus>('loading')
  const [options, setOptions] = useState<FlightOption[]>([])
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [token, setToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setStatus('loading')
    setErrorMessage(null)

    get<unknown[]>('/flights/options')
      .then((raw) => {
        if (cancelled) return
        const normalized = Array.isArray(raw) ? raw.map(normalizeOption).filter((o): o is FlightOption => o !== null) : []
        setOptions(normalized)
        setStatus('ready')
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (err instanceof ApiError && (err.status === 404 || err.status === 0)) {
          setStatus('unavailable')
          return
        }
        setStatus('error')
        setErrorMessage(err instanceof ApiError ? err.message : 'Could not load flyable routes.')
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const refetch = useCallback(() => setToken((t) => t + 1), [])

  return { status, options, errorMessage, refetch }
}
