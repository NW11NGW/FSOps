import { useEffect, useState } from 'react'
import { Compass, Gauge, MapPin, Route as RouteIcon, Tag } from 'lucide-react'

import { AirportSearch } from '@/components/shared/AirportSearch'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { ApiError, get } from '@/lib/api'
import type { AirportDetail, AirportSummary } from '@/types/airport'

type DetailStatus = 'loading' | 'success' | 'error'

const ELEVATION_FORMATTER = new Intl.NumberFormat('en-US')
const LENGTH_FORMATTER = new Intl.NumberFormat('en-US')

function formatCoordinate(latitude: number, longitude: number): string {
  const latDir = latitude >= 0 ? 'N' : 'S'
  const lonDir = longitude >= 0 ? 'E' : 'W'
  return `${Math.abs(latitude).toFixed(4)}°${latDir}, ${Math.abs(longitude).toFixed(4)}°${lonDir}`
}

function describeDetailError(err: unknown): string {
  if (err instanceof ApiError) {
    return err.status === 404 ? 'This airport could not be found in the database.' : err.message
  }
  return 'Could not load airport details. Check your connection and try again.'
}

export function RoutesPage() {
  const [selected, setSelected] = useState<AirportSummary | null>(null)
  const [detail, setDetail] = useState<AirportDetail | null>(null)
  const [status, setStatus] = useState<DetailStatus>('loading')
  const [errorMessage, setErrorMessage] = useState('')

  useEffect(() => {
    if (!selected) return

    const controller = new AbortController()
    setStatus('loading')
    setErrorMessage('')

    get<AirportDetail>(`/airports/${selected.icao}`, undefined, { signal: controller.signal })
      .then((data) => {
        setDetail(data)
        setStatus('success')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setStatus('error')
        setErrorMessage(describeDetailError(err))
      })

    return () => controller.abort()
  }, [selected])

  return (
    <div className="space-y-4">
      <PageHeader title="Routes" description="Plan and manage the routes your airline flies." />

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Find an airport</CardTitle>
          <p className="text-sm text-muted-foreground">
            Search the world airport database — this will anchor your home base and route endpoints once route
            building arrives.
          </p>
        </CardHeader>
        <CardContent>
          <AirportSearch onSelect={setSelected} />
        </CardContent>
      </Card>

      {selected && (
        <Card>
          <CardHeader className="flex-row items-start justify-between gap-4 space-y-0">
            <div className="min-w-0">
              <CardTitle className="flex flex-wrap items-center gap-2 text-base">
                <span className="font-mono">{selected.icao}</span>
                {selected.iata && <Badge variant="outline">{selected.iata}</Badge>}
              </CardTitle>
              <p className="mt-1 text-sm text-muted-foreground">{selected.name}</p>
            </div>
            {status === 'success' && detail && (
              <Badge variant={detail.hasScheduledService ? 'success' : 'muted'} className="shrink-0">
                {detail.hasScheduledService ? 'Scheduled service' : 'No scheduled service'}
              </Badge>
            )}
          </CardHeader>
          <CardContent className="space-y-5">
            {status === 'loading' && (
              <div className="space-y-3">
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                  <Skeleton className="h-12 w-full" />
                  <Skeleton className="h-12 w-full" />
                  <Skeleton className="h-12 w-full" />
                  <Skeleton className="h-12 w-full" />
                </div>
                <Skeleton className="h-32 w-full" />
              </div>
            )}

            {status === 'error' && <p className="text-sm text-danger">{errorMessage}</p>}

            {status === 'success' && detail && (
              <>
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                  <div className="space-y-1">
                    <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <MapPin className="size-3.5" /> Location
                    </p>
                    <p className="text-sm">{[detail.municipality, detail.country].filter(Boolean).join(', ') || '—'}</p>
                  </div>
                  <div className="space-y-1">
                    <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <Compass className="size-3.5" /> Coordinates
                    </p>
                    <p className="font-mono text-sm tabular-nums">{formatCoordinate(detail.latitude, detail.longitude)}</p>
                  </div>
                  <div className="space-y-1">
                    <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <Gauge className="size-3.5" /> Elevation
                    </p>
                    <p className="text-sm tabular-nums">{ELEVATION_FORMATTER.format(detail.elevationFt)} ft</p>
                  </div>
                  <div className="space-y-1">
                    <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wide text-muted-foreground">
                      <Tag className="size-3.5" /> Size category
                    </p>
                    <p className="text-sm">{detail.sizeCategory}</p>
                  </div>
                </div>

                {detail.runways.length > 0 ? (
                  <div>
                    <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Runways</p>
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>Designator</TableHead>
                          <TableHead>Length</TableHead>
                          <TableHead>Width</TableHead>
                          <TableHead>Surface</TableHead>
                          <TableHead>Heading</TableHead>
                          <TableHead>Status</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {detail.runways.map((runway) => (
                          <TableRow key={runway.designator}>
                            <TableCell className="font-mono">{runway.designator}</TableCell>
                            <TableCell className="tabular-nums">{LENGTH_FORMATTER.format(runway.lengthFt)} ft</TableCell>
                            <TableCell className="tabular-nums">{runway.widthFt} ft</TableCell>
                            <TableCell>{runway.surface}</TableCell>
                            <TableCell className="tabular-nums">{runway.headingTrue}°</TableCell>
                            <TableCell>
                              <Badge variant={runway.isClosed ? 'danger' : runway.isLighted ? 'success' : 'muted'}>
                                {runway.isClosed ? 'Closed' : runway.isLighted ? 'Lighted' : 'Unlighted'}
                              </Badge>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                ) : (
                  <p className="text-sm text-muted-foreground">No runway data available for this airport.</p>
                )}
              </>
            )}
          </CardContent>
        </Card>
      )}

      <EmptyState icon={RouteIcon} title="No routes yet" description="Nothing here yet — this arrives in a later update." />
    </div>
  )
}
