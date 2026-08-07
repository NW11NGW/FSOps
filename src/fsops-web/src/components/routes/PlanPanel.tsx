import { AlertTriangle, Clock3, Compass, Fuel, Gauge, Route as RouteIcon, Ruler, Tag, TriangleAlert } from 'lucide-react'

import { StatTile } from '@/components/shared/StatTile'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useSettings } from '@/hooks/useSettings'
import type { RoutePreviewStatus } from '@/hooks/useRoutePreview'
import { cn } from '@/lib/utils'
import type { AirportSummary } from '@/types/airport'
import type { RoutePreviewResponse } from '@/types/route'

interface PlanPanelProps {
  departure: AirportSummary | null
  arrival: AirportSummary | null
  preview: RoutePreviewResponse | null
  status: RoutePreviewStatus
  isRefreshing: boolean
  errorMessage: string | null
  dangerMessage: string | null
  advisoryWarnings: string[]
  fareOverrideValue: string
  onFareOverrideChange: (value: string) => void
  onCreate: () => void
  creating: boolean
  createError: string | null
  canCreate: boolean
}

function BreakdownRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between gap-2 text-sm">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="tabular-nums font-medium">{value}</dd>
    </div>
  )
}

/** Live plan panel driven by /routes/preview: stat tiles, warnings, breakdown detail, and the create action. */
export function PlanPanel({
  departure,
  arrival,
  preview,
  status,
  isRefreshing,
  errorMessage,
  dangerMessage,
  advisoryWarnings,
  fareOverrideValue,
  onFareOverrideChange,
  onCreate,
  creating,
  createError,
  canCreate,
}: PlanPanelProps) {
  const { fmt, currentCurrency } = useSettings()

  if (!departure || !arrival) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-2 py-12 text-center">
          <RouteIcon className="size-8 text-muted-foreground" />
          <p className="text-sm font-medium">Pick both airports to see a live plan</p>
          <p className="text-sm text-muted-foreground">
            Distance, block time, fuel, and a suggested fare will appear here.
          </p>
        </CardContent>
      </Card>
    )
  }

  const showSkeleton = status === 'loading' && !preview
  const showErrorOnly = status === 'error' && !preview

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Live plan</CardTitle>
        <p className="font-mono text-sm text-muted-foreground">
          {departure.icao} → {arrival.icao}
        </p>
      </CardHeader>
      <CardContent className="space-y-4">
        {showErrorOnly && <p className="text-sm text-danger">{errorMessage}</p>}

        {showSkeleton && (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {Array.from({ length: 6 }).map((_, index) => (
              <Skeleton key={index} className="h-[86px] w-full" />
            ))}
          </div>
        )}

        {preview && (
          <div className={cn('space-y-4 transition-opacity duration-200', isRefreshing && 'opacity-60')}>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              <StatTile label="Distance" icon={Ruler} value={fmt.distance(preview.distanceNm)} />
              <StatTile label="Initial bearing" icon={Compass} value={`${Math.round(preview.initialBearingDeg)}°`} />
              <StatTile label="Block time" icon={Clock3} value={fmt.duration(preview.estimatedBlockMinutes)} />
              <StatTile label="Cruise altitude" icon={Gauge} value={fmt.altitude(preview.cruiseAltitudeFt)} />
              <StatTile label="Block fuel" icon={Fuel} value={fmt.weight(preview.blockFuelKg)} />
              <StatTile label="Suggested fare" icon={Tag} value={fmt.money(preview.suggestedFare)} />
            </div>

            {dangerMessage && (
              <div className="flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 p-3 text-sm">
                <TriangleAlert className="mt-0.5 size-4 shrink-0 text-danger" />
                <div>
                  <p className="font-medium text-danger">This route can&rsquo;t be created yet</p>
                  <p className="mt-0.5 text-danger/90">{dangerMessage}</p>
                </div>
              </div>
            )}

            {advisoryWarnings.length > 0 && (
              <div className="space-y-1.5 rounded-md border border-warning/40 bg-warning/10 p-3 text-sm text-warning">
                {advisoryWarnings.map((warning) => (
                  <div key={warning} className="flex items-start gap-2">
                    <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
                    <p>{warning}</p>
                  </div>
                ))}
              </div>
            )}

            {preview.blockTimeBreakdown && preview.fuelBreakdown && (
              <Tabs defaultValue="time">
                <TabsList>
                  <TabsTrigger value="time">Block time</TabsTrigger>
                  <TabsTrigger value="fuel">Fuel</TabsTrigger>
                </TabsList>
                <TabsContent value="time">
                  <dl className="grid grid-cols-1 gap-x-6 gap-y-1.5 rounded-md border border-border p-3 sm:grid-cols-2">
                    <BreakdownRow label="Taxi out" value={fmt.duration(preview.blockTimeBreakdown.taxiOutMinutes)} />
                    <BreakdownRow label="Climb" value={fmt.duration(preview.blockTimeBreakdown.climbMinutes)} />
                    <BreakdownRow label="Cruise" value={fmt.duration(preview.blockTimeBreakdown.cruiseMinutes)} />
                    <BreakdownRow label="Descent" value={fmt.duration(preview.blockTimeBreakdown.descentMinutes)} />
                    <BreakdownRow label="Taxi in" value={fmt.duration(preview.blockTimeBreakdown.taxiInMinutes)} />
                    <div className="col-span-full mt-1 flex items-center justify-between border-t border-border pt-1.5 text-sm">
                      <dt className="font-medium">Total</dt>
                      <dd className="tabular-nums font-semibold">{fmt.duration(preview.blockTimeBreakdown.totalMinutes)}</dd>
                    </div>
                  </dl>
                </TabsContent>
                <TabsContent value="fuel">
                  <dl className="grid grid-cols-1 gap-x-6 gap-y-1.5 rounded-md border border-border p-3 sm:grid-cols-2">
                    <BreakdownRow label="Trip fuel" value={fmt.weight(preview.fuelBreakdown.tripFuelKg)} />
                    <BreakdownRow label="Taxi fuel" value={fmt.weight(preview.fuelBreakdown.taxiFuelKg)} />
                    <BreakdownRow label="Contingency" value={fmt.weight(preview.fuelBreakdown.contingencyFuelKg)} />
                    <BreakdownRow label="Alternate" value={fmt.weight(preview.fuelBreakdown.alternateFuelKg)} />
                    <BreakdownRow label="Final reserve" value={fmt.weight(preview.fuelBreakdown.finalReserveFuelKg)} />
                    <div className="col-span-full mt-1 flex items-center justify-between border-t border-border pt-1.5 text-sm">
                      <dt className="font-medium">Total</dt>
                      <dd className="tabular-nums font-semibold">{fmt.weight(preview.fuelBreakdown.totalFuelKg)}</dd>
                    </div>
                  </dl>
                </TabsContent>
              </Tabs>
            )}

            <div className="space-y-2 border-t border-border pt-4">
              <div className="flex flex-wrap items-end justify-between gap-3">
                <div className="space-y-1.5">
                  <Label htmlFor="fare-override">Fare override</Label>
                  <div className="relative w-40">
                    {currentCurrency.symbolBefore && (
                      <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-sm text-muted-foreground">
                        {currentCurrency.symbol}
                      </span>
                    )}
                    <Input
                      id="fare-override"
                      type="number"
                      min={0}
                      step="0.01"
                      inputMode="decimal"
                      value={fareOverrideValue}
                      onChange={(event) => onFareOverrideChange(event.target.value)}
                      className={currentCurrency.symbolBefore ? 'pl-7' : 'pr-7'}
                    />
                    {!currentCurrency.symbolBefore && (
                      <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-sm text-muted-foreground">
                        {currentCurrency.symbol}
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground">Defaults to the suggested fare — edit to override.</p>
                </div>
                <Button type="button" onClick={onCreate} disabled={!canCreate}>
                  {creating ? 'Creating…' : 'Create route'}
                </Button>
              </div>
              {createError && <p className="text-sm text-danger">{createError}</p>}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
