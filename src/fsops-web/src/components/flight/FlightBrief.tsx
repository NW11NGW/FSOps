import { Clock3, Fuel, Gauge, PlaneTakeoff, Ruler, Tag, TriangleAlert, Users } from 'lucide-react'
import { Link } from 'react-router-dom'

import { ReadinessChecks } from '@/components/flight/ReadinessChecks'
import { SimBriefActions } from '@/components/flight/SimBriefActions'
import type { AircraftOptionRow, RouteRow } from '@/components/flight/routeRow'
import { StatTile } from '@/components/shared/StatTile'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useSettings } from '@/hooks/useSettings'
import type { FlightPreviewStatus } from '@/hooks/useFlightPreview'
import { formatCallsign } from '@/lib/callsign'
import { cn } from '@/lib/utils'
import type { SimStatus, TelemetryPayload } from '@/types/flight'
import type { RoutePreviewResponse } from '@/types/route'

interface FlightBriefProps {
  row: RouteRow
  /** Every aircraft physically at the departure airport - flyable or not. (The
   *  2026-08-09 defect this fixes was a route offering only one of four aircraft actually parked
   *  there, and separately, hiding unflyable ones instead of showing why). */
  aircraftOptions: AircraftOptionRow[]
  selectedAircraft: AircraftOptionRow | null
  onSelectAircraft: (aircraft: AircraftOptionRow) => void
  preview: RoutePreviewResponse | null
  previewStatus: FlightPreviewStatus
  airlineIcaoCode: string | null
  simStatus: SimStatus | null
  simStatusLoaded: boolean
  telemetry: TelemetryPayload | null
  departureCoords: { icao: string; latitude: number; longitude: number } | null
  onStart: () => void
  starting: boolean
  startError: string | null
}

/** How much pricier the destination's fuel must be (as a multiple of the departure price) before
 *  the brief bothers surfacing a hint - avoids noise over a trivial day-to-day price-walk wobble. */
const DestinationPriceHintThreshold = 1.15

function BreakdownRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between gap-2 text-sm">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="tabular-nums font-medium">{value}</dd>
    </div>
  )
}

/** The pre-flight plan: aircraft choice, key stats, fuel/time breakdown, SimBrief hand-off, and the start action. */
export function FlightBrief({
  row,
  aircraftOptions,
  selectedAircraft,
  onSelectAircraft,
  preview,
  previewStatus,
  airlineIcaoCode,
  simStatus,
  simStatusLoaded,
  telemetry,
  departureCoords,
  onStart,
  starting,
  startError,
}: FlightBriefProps) {
  const { fmt } = useSettings()

  const paxCapacity = selectedAircraft?.paxCapacity ?? null
  // From the real demand/fare engine (see RouteEconomicsPreview) - not every seat sold at fare.
  const economics = preview?.economics ?? null
  const expectedPassengers = economics?.expectedPassengers ?? null
  const expectedRevenue = economics?.expectedRevenuePerSector ?? null
  const callsign = formatCallsign(airlineIcaoCode, row.flightNumber)

  const summaryLines = [
    `${row.departureIcao} → ${row.arrivalIcao}${callsign ? ` (${callsign})` : ''}`,
    preview ? `Distance: ${fmt.distance(preview.distanceNm)}` : `Distance: ${fmt.distance(row.distanceNm)}`,
    preview ? `Cruise altitude: ${fmt.altitude(preview.cruiseAltitudeFt)}` : null,
    preview ? `Block time: ${fmt.duration(preview.estimatedBlockMinutes)}` : row.blockMinutes !== null ? `Block time: ${fmt.duration(row.blockMinutes)}` : null,
    preview ? `Block fuel: ${fmt.weight(preview.blockFuelKg)}` : null,
    selectedAircraft ? `Aircraft: ${selectedAircraft.registration} (${selectedAircraft.icaoType})` : null,
  ].filter((line): line is string => Boolean(line))

  // Destination fuel is materially pricier than here - worth flagging before departure, since
  // tankering is only a real decision if the price gap is visible in advance.
  // Computed once rather than inline in JSX
  // so the null-narrowing stays simple and correct.
  const departureFuelPrice = preview?.fuelPricePerKg ?? null
  const destinationFuelPrice = preview?.destinationFuelPricePerKg ?? null
  const showDestinationPriceHint =
    departureFuelPrice !== null && destinationFuelPrice !== null && destinationFuelPrice > departureFuelPrice * DestinationPriceHintThreshold
  const tankeringAdvisory = preview?.tankeringAdvisory ?? null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <PlaneTakeoff className="size-4 text-accent" />
          Flight brief
        </CardTitle>
        <p className="font-mono text-sm text-muted-foreground">
          {row.departureIcao} → {row.arrivalIcao}
          {callsign && <span className="ml-2 font-sans text-xs">· {callsign}</span>}
        </p>
      </CardHeader>
      <CardContent className="space-y-5">
        {aircraftOptions.length > 0 && (
          <div className="space-y-2">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Aircraft</p>
            <div className="flex flex-wrap gap-2">
              {aircraftOptions.map((aircraft) => {
                const selected = aircraft.fleetAircraftId === selectedAircraft?.fleetAircraftId
                return (
                  <button
                    key={aircraft.fleetAircraftId}
                    type="button"
                    disabled={!aircraft.isFlyable}
                    onClick={() => aircraft.isFlyable && onSelectAircraft(aircraft)}
                    title={aircraft.isFlyable ? undefined : (aircraft.reason ?? undefined)}
                    className={cn(
                      'rounded-md border px-3 py-1.5 text-left text-xs transition-colors',
                      !aircraft.isFlyable && 'cursor-not-allowed border-dashed border-border bg-muted/20 text-muted-foreground opacity-70',
                      aircraft.isFlyable && selected && 'border-accent bg-accent/10 text-foreground',
                      aircraft.isFlyable && !selected && 'border-border text-muted-foreground hover:border-accent/50',
                    )}
                  >
                    <span className="block font-mono font-semibold">{aircraft.registration || '—'}</span>
                    <span>{aircraft.aircraftTypeName || aircraft.icaoType || aircraft.family}</span>
                  </button>
                )
              })}
            </div>
            {/* Every unflyable aircraft's reason, verbatim from the backend, never omitted
             *  (2026-08-09 defect: aircraft the player could not fly used to vanish instead of
             *  explaining why). Reservation is the single most common one, so it links straight to
             *  the Fleet page's reserve/release control rather than making the player go find it. */}
            {aircraftOptions.some((a) => !a.isFlyable) && (
              <ul className="space-y-1">
                {aircraftOptions
                  .filter((a) => !a.isFlyable)
                  .map((aircraft) => (
                    <li key={aircraft.fleetAircraftId} className="flex items-start gap-1.5 text-xs text-muted-foreground">
                      <TriangleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden="true" />
                      <span className="min-w-0 break-words">
                        {aircraft.reason}
                        {aircraft.reason?.toLowerCase().includes('fleet page') && (
                          <>
                            {' '}
                            <Link to="/fleet" className="font-medium text-accent underline-offset-2 hover:underline">
                              Open the Fleet page
                            </Link>
                          </>
                        )}
                      </span>
                    </li>
                  ))}
              </ul>
            )}
          </div>
        )}
        {aircraftOptions.length === 0 && (
          <p className="rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
            {row.aircraftUnknown
              ? "Aircraft assignment isn't known for this route yet — starting the flight will use your fleet's first available aircraft automatically."
              : (row.reason ?? 'No aircraft is at this departure airport.')}
          </p>
        )}

        <div>
          {previewStatus === 'loading' && !preview ? (
            <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-3">
              {Array.from({ length: 6 }).map((_, index) => (
                <Skeleton key={index} className="h-[86px] w-full" />
              ))}
            </div>
          ) : (
            <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-3">
              <StatTile label="Distance" icon={Ruler} value={fmt.distance(preview?.distanceNm ?? row.distanceNm)} />
              <StatTile
                label="Cruise altitude"
                icon={Gauge}
                value={preview ? fmt.altitude(preview.cruiseAltitudeFt) : undefined}
              />
              <StatTile
                label="Block time"
                icon={Clock3}
                value={preview ? fmt.duration(preview.estimatedBlockMinutes) : row.blockMinutes !== null ? fmt.duration(row.blockMinutes) : undefined}
              />
              <StatTile label="Block fuel" icon={Fuel} value={preview ? fmt.weight(preview.blockFuelKg) : undefined} />
              <StatTile
                label="Passengers"
                icon={Users}
                value={expectedPassengers !== null ? `${expectedPassengers} / ${paxCapacity ?? '—'}` : '—'}
              />
              <StatTile
                label="Expected revenue"
                icon={Tag}
                value={expectedRevenue !== null ? fmt.money(expectedRevenue) : '—'}
              />
            </div>
          )}
          {economics !== null && (
            <p className="mt-1.5 text-xs text-muted-foreground">
              {economics.loadFactorPercent.toFixed(0)}% load factor at {fmt.money(economics.fare)} — today's demand model,
              actual boardings may vary.
            </p>
          )}
        </div>

        {preview?.blockTimeBreakdown && preview.fuelBreakdown && (
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
                {preview.fuelPricePerKg !== null && (
                  <BreakdownRow label="Price here" value={`${fmt.money(preview.fuelPricePerKg)}/kg`} />
                )}
                {preview.destinationFuelPricePerKg !== null && (
                  <BreakdownRow label="Price at destination" value={`${fmt.money(preview.destinationFuelPricePerKg)}/kg`} />
                )}
              </dl>
            </TabsContent>
          </Tabs>
        )}

        {showDestinationPriceHint && departureFuelPrice !== null && destinationFuelPrice !== null && (
          <div className="flex items-start gap-2 rounded-md border border-accent/30 bg-accent/5 p-3 text-xs text-muted-foreground">
            <Fuel className="mt-0.5 size-3.5 shrink-0 text-accent" />
            <p className="min-w-0 break-words">
              Fuel is {(((destinationFuelPrice - departureFuelPrice) / departureFuelPrice) * 100).toFixed(0)}% more expensive at{' '}
              {row.arrivalIcao} than here.
              {tankeringAdvisory?.recommended && (
                <>
                  {' '}Uplifting an extra ~{fmt.weight(tankeringAdvisory.extraFuelToCarryKg)} here to cover the return leg could
                  save about {fmt.money(tankeringAdvisory.netSavingAmount)}, after the extra weight you&rsquo;d burn carrying
                  it{tankeringAdvisory.exceedsMtow ? ' — but check this stays under maximum take-off weight' : ''}.
                </>
              )}
              {tankeringAdvisory && !tankeringAdvisory.recommended && (
                <> The extra weight you&rsquo;d burn carrying it outweighs the saving, so refuelling at the destination is
                  still cheaper overall.</>
              )}
            </p>
          </div>
        )}

        <Separator />

        <div className="space-y-2">
          <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Readiness</p>
          <ReadinessChecks
            simStatus={simStatus}
            simStatusLoaded={simStatusLoaded}
            telemetry={telemetry}
            selectedAircraft={selectedAircraft}
            departure={departureCoords}
          />
        </div>

        <Separator />

        <SimBriefActions
          params={{
            originIcao: row.departureIcao,
            destinationIcao: row.arrivalIcao,
            aircraftIcaoType: selectedAircraft?.icaoType ?? null,
            airlineIcaoCode,
            flightNumber: row.flightNumber,
            registration: selectedAircraft?.registration ?? null,
            passengers: paxCapacity,
          }}
          summaryLines={summaryLines}
        />

        <div className="space-y-2 border-t border-border pt-4">
          <Button
            type="button"
            size="lg"
            onClick={onStart}
            disabled={starting || (!row.aircraftUnknown && !row.isFlyable)}
            className="w-full gap-2"
          >
            {starting ? (
              'Starting…'
            ) : (
              <>
                <PlaneTakeoff className="size-4" />
                Start flight
              </>
            )}
          </Button>
          {startError && <p className="text-sm text-danger">{startError}</p>}
          {row.aircraftUnknown && (
            <Badge variant="muted" className="w-fit">
              Aircraft availability unknown — this uses your fleet default
            </Badge>
          )}
        </div>
      </CardContent>
    </Card>
  )
}
