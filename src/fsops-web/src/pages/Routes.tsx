import { useEffect, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Building2 } from 'lucide-react'
import { toast } from 'sonner'

import { RouteMap } from '@/components/map/RouteMap'
import { AirportPickerCard } from '@/components/routes/AirportPickerCard'
import { PlanPanel } from '@/components/routes/PlanPanel'
import { RoutesTable } from '@/components/routes/RoutesTable'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Skeleton } from '@/components/ui/skeleton'
import { useRouteBlockTimes } from '@/hooks/useRouteBlockTimes'
import { useRoutePreview } from '@/hooks/useRoutePreview'
import { useRoutes } from '@/hooks/useRoutes'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, del, get, post } from '@/lib/api'
import type { AirportDetail, AirportSummary } from '@/types/airport'
import type { LiveContext } from '@/types/live-context'
import type { CurrencyInfo } from '@/types/settings'
import type { RouteSummary } from '@/types/route'

function toDisplayAmount(baseAmount: number, currency: Pick<CurrencyInfo, 'rate' | 'decimalPlaces'>): string {
  return (baseAmount * currency.rate).toFixed(currency.decimalPlaces)
}

export function RoutesPage() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const { currentCurrency } = useSettings()

  const [departure, setDeparture] = useState<AirportSummary | null>(null)
  const [arrival, setArrival] = useState<AirportSummary | null>(null)
  const [fareOverride, setFareOverride] = useState('')
  const [fareTouched, setFareTouched] = useState(false)
  const [creating, setCreating] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [selectedRouteId, setSelectedRouteId] = useState<string | null>(null)

  const preview = useRoutePreview(departure?.icao ?? null, arrival?.icao ?? null)
  const routesQuery = useRoutes()
  const blockMinutes = useRouteBlockTimes(routesQuery.routes)

  // A freshly picked city pair should always start from the suggested fare - only stop tracking
  // it once the user actually edits the override field themselves.
  useEffect(() => {
    setFareTouched(false)
  }, [departure?.icao, arrival?.icao])

  useEffect(() => {
    if (!fareTouched && preview.data) {
      setFareOverride(toDisplayAmount(preview.data.suggestedFare, currentCurrency))
    }
  }, [preview.data, fareTouched, currentCurrency])

  function swap() {
    setDeparture(arrival)
    setArrival(departure)
    setSelectedRouteId(null)
  }

  function selectDeparture(airport: AirportSummary) {
    setDeparture(airport)
    setSelectedRouteId(null)
  }

  function selectArrival(airport: AirportSummary) {
    setArrival(airport)
    setSelectedRouteId(null)
  }

  function clearDeparture() {
    setDeparture(null)
    setSelectedRouteId(null)
  }

  function clearArrival() {
    setArrival(null)
    setSelectedRouteId(null)
  }

  async function handleSelectRoute(route: RouteSummary) {
    setSelectedRouteId(route.id)
    try {
      const [dep, arr] = await Promise.all([
        get<AirportDetail>(`/airports/${route.departureIcao}`),
        get<AirportDetail>(`/airports/${route.arrivalIcao}`),
      ])
      setDeparture(dep)
      setArrival(arr)
    } catch {
      toast.error("Could not load this route's airports.")
    }
  }

  async function handleDeleteRoute(route: RouteSummary) {
    await del(`/routes/${route.id}`)
    if (selectedRouteId === route.id) setSelectedRouteId(null)
    toast.success(`Route ${route.departureIcao} → ${route.arrivalIcao} deleted.`)
    routesQuery.refetch()
  }

  async function handleCreate() {
    if (!departure || !arrival || !preview.data) return
    setCreating(true)
    setCreateError(null)

    const parsedFare = Number(fareOverride)
    const baseFare =
      fareTouched && Number.isFinite(parsedFare) && parsedFare > 0 ? parsedFare / currentCurrency.rate : undefined

    try {
      await post<RouteSummary>('/routes', {
        departureIcao: departure.icao,
        arrivalIcao: arrival.icao,
        ...(baseFare !== undefined ? { baseFare } : {}),
      })
      toast.success(`Route ${departure.icao} → ${arrival.icao} created.`)
      setFareTouched(false)
      routesQuery.refetch()
    } catch (err) {
      setCreateError(
        err instanceof ApiError ? err.message : 'Could not create this route. Check your connection and try again.',
      )
    } finally {
      setCreating(false)
    }
  }

  const sameAirport = Boolean(preview.data?.validation.sameAirport)
  const outOfRange = Boolean(preview.data && !preview.data.validation.withinRange)

  const dangerMessage = sameAirport
    ? 'Departure and arrival are the same airport — pick two different airports to build a route.'
    : outOfRange
      ? (preview.data?.validation.warnings.find((warning) => /range/i.test(warning)) ??
        "This route is beyond the aircraft's practical operating range.")
      : null

  const advisoryWarnings = (preview.data?.validation.warnings ?? []).filter((warning) => {
    if (sameAirport && /same airport/i.test(warning)) return false
    if (outOfRange && /range/i.test(warning)) return false
    return true
  })

  const canCreate = Boolean(departure && arrival && preview.data && !sameAirport && !outOfRange && !creating)
  const routePath = preview.data?.greatCirclePath ?? []

  if (airlineSummary.status === 'loading') {
    return (
      <div className="space-y-4">
        <PageHeader title="Routes" description="Plan and manage the routes your airline flies." />
        <Skeleton className="h-72 w-full" />
      </div>
    )
  }

  if (airlineSummary.status === 'error' || !airlineSummary.data) {
    return (
      <div className="space-y-4">
        <PageHeader title="Routes" description="Plan and manage the routes your airline flies." />
        <EmptyState
          icon={Building2}
          title="No airline yet"
          description="Set up your airline before you can plan routes."
        />
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <PageHeader title="Routes" description="Plan and manage the routes your airline flies." />

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_440px] lg:items-start">
        <div className="min-w-0 space-y-4">
          <AirportPickerCard
            departure={departure}
            arrival={arrival}
            onSelectDeparture={selectDeparture}
            onSelectArrival={selectArrival}
            onClearDeparture={clearDeparture}
            onClearArrival={clearArrival}
            onSwap={swap}
          />
          <PlanPanel
            departure={departure}
            arrival={arrival}
            preview={preview.data}
            status={preview.status}
            isRefreshing={preview.isRefreshing}
            errorMessage={preview.errorMessage}
            dangerMessage={dangerMessage}
            advisoryWarnings={advisoryWarnings}
            fareOverrideValue={fareOverride}
            onFareOverrideChange={(value) => {
              setFareTouched(true)
              setFareOverride(value)
            }}
            onCreate={handleCreate}
            creating={creating}
            createError={createError}
            canCreate={canCreate}
          />
        </div>

        <RouteMap
          departure={departure}
          arrival={arrival}
          path={routePath}
          className="h-[360px] lg:sticky lg:top-6 lg:h-[calc(100vh-176px)]"
        />
      </div>

      <RoutesTable
        routes={routesQuery.routes}
        status={routesQuery.status}
        blockMinutes={blockMinutes}
        selectedId={selectedRouteId}
        onSelect={handleSelectRoute}
        onDelete={handleDeleteRoute}
      />
    </div>
  )
}
