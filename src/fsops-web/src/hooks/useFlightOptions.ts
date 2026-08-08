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

function str(raw: Raw, ...keys: string[]): string | null {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'string' && value.length > 0) return value
  }
  return null
}

function num(raw: Raw, ...keys: string[]): number | null {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'number' && Number.isFinite(value)) return value
  }
  return null
}

function bool(raw: Raw, fallback: boolean, ...keys: string[]): boolean {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'boolean') return value
  }
  return fallback
}

function normalizeAircraft(raw: unknown): FlightOptionAircraft | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Raw
  const fleetAircraftId = str(r, 'fleetAircraftId', 'id')
  if (!fleetAircraftId) return null
  return {
    fleetAircraftId,
    registration: str(r, 'registration', 'aircraftRegistration', 'reg') ?? '',
    aircraftTypeId: str(r, 'aircraftTypeId', 'typeId') ?? '',
    aircraftTypeName: str(r, 'aircraftTypeName', 'typeName', 'name') ?? '',
    icaoType: str(r, 'icaoType', 'aircraftIcaoType', 'icaoTypeCode', 'type') ?? '',
    family: str(r, 'family', 'aircraftFamily') ?? '',
    locationIcao: str(r, 'locationIcao', 'location') ?? '',
    paxCapacity: num(r, 'paxCapacity', 'paxCap', 'seats'),
  }
}

/**
 * GET /flights/options' DTO shape was settling while this screen was being built alongside the
 * backend. The live shape (verified) is flat - `fleetAircraftId` + `aircraftRegistration` sit
 * directly on the option, not nested in an aircraft array, and it carries no type/pax info at
 * all - so both shapes are handled: a nested array if the backend ever grows one (kept for
 * forward compatibility), else a single aircraft synthesised from the flat fields. Its
 * location defaults to the route's departure, matching the endpoint's own contract ("the fleet
 * aircraft available at that route's departure airport").
 */
function normalizeOption(raw: unknown): FlightOption | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Raw
  const routeId = str(r, 'routeId', 'id')
  const departureIcao = str(r, 'departureIcao', 'departure')
  const arrivalIcao = str(r, 'arrivalIcao', 'arrival')
  if (!routeId || !departureIcao || !arrivalIcao) return null

  const rawAircraft = r.availableAircraft ?? r.aircraft ?? r.fleetAircraft
  let availableAircraft = Array.isArray(rawAircraft)
    ? rawAircraft.map(normalizeAircraft).filter((a): a is FlightOptionAircraft => a !== null)
    : []

  if (availableAircraft.length === 0) {
    const flatFleetAircraftId = str(r, 'fleetAircraftId')
    if (flatFleetAircraftId) {
      const flat = normalizeAircraft(r)
      if (flat) {
        availableAircraft = [{ ...flat, locationIcao: flat.locationIcao || departureIcao }]
      }
    }
  }

  return {
    routeId,
    flightNumber: str(r, 'flightNumber', 'flightNo'),
    departureIcao,
    departureName: str(r, 'departureName'),
    arrivalIcao,
    arrivalName: str(r, 'arrivalName'),
    distanceNm: num(r, 'distanceNm') ?? 0,
    blockMinutes: num(r, 'blockMinutes', 'estimatedBlockMinutes', 'plannedBlockMinutes'),
    isFlyable: bool(r, availableAircraft.length > 0, 'isFlyable', 'flyable', 'canFly'),
    reason: str(r, 'reason', 'unavailableReason', 'blockedReason'),
    availableAircraft,
  }
}

/**
 * GET /flights/options - the flyable-routes list for the Fly screen. Treated as an optional,
 * still-arriving capability: a 404 (endpoint not deployed yet) reports as 'unavailable' rather
 * than 'error' so the page can degrade to a simpler route list instead of showing a scary error.
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
