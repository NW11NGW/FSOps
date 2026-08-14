import { lazy, Suspense, useMemo } from 'react'
import { MapPinOff, Route as RouteIcon } from 'lucide-react'

import { EmptyState } from '@/components/shared/EmptyState'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useAirportCoordinates } from '@/hooks/useAirportCoordinates'
import { useFlightTrack } from '@/hooks/useFlightTrack'
import { useSettings } from '@/hooks/useSettings'
import { sampleGreatCirclePath } from '@/lib/geo'
import type { LonLat } from '@/types/route'

// maplibre-gl only ever loads through a dynamic import - see vite.config.ts's manualChunks note.
const FlownTrackMap = lazy(() => import('@/components/map/FlownTrackMap').then((m) => ({ default: m.FlownTrackMap })))

interface FlightTrackCardProps {
  flightId: string | null
  departureIcao: string | null
  arrivalIcao: string | null
  /** The distance the great circle covers, for the "how far off the line did you go" comparison.
   *  Optional - the card works without it. */
  className?: string
}

/**
 * The path a flight actually flew, on its own record.
 *
 * FSOps has recorded a position roughly every 15 seconds of every tracked flight since tracking
 * existed, and until now has never shown a single one of them. This is where they earn their place:
 * the vectors, the hold, the long downwind and the go-around are all in there, and none of it is
 * visible in a table of OOOI times.
 *
 * <b>Every awkward case degrades to a sentence, never a broken map:</b>
 * <ul>
 *   <li><b>No track at all</b> - older flights flown before snapshots existed, and every
 *   virtual-pilot sector, which never had a simulator attached and recorded nothing. Says so
 *   plainly, and says why.</li>
 *   <li><b>One or two points</b> - the sim disconnected almost immediately. One point is a position,
 *   not a path, so it is drawn as a marker with no line through it.</li>
 *   <li><b>A track crossing the antimeridian</b> - split into separate segments before drawing (see
 *   lib/geo), so a Pacific crossing draws as two arcs at the map edges instead of one line the
 *   wrong way round the planet.</li>
 *   <li><b>An opening the simulator had not yet worked out</b> - SimConnect reports roughly 0.0N
 *   90.0E before it has a real aircraft state, and those samples were written into real flights.
 *   They are not drawn (see FlownTrackBuilder), and the card says how many and why rather than
 *   quietly showing fewer positions than it claims were recorded.</li>
 * </ul>
 */
export function FlightTrackCard({ flightId, departureIcao, arrivalIcao, className }: FlightTrackCardProps) {
  const { fmt } = useSettings()
  const { status, track } = useFlightTrack(flightId)

  const neededIcaos = useMemo(
    () => [departureIcao, arrivalIcao].filter((icao): icao is string => Boolean(icao)),
    [departureIcao, arrivalIcao],
  )
  const coordsByIcao = useAirportCoordinates(neededIcaos)

  // The airports themselves, which is where the labelled markers go - never an end of the point
  // list. Memoised because they are passed to the map as props and a fresh array each render would
  // resync it on every paint.
  const departureAt = useMemo<LonLat | null>(() => {
    const from = departureIcao ? coordsByIcao[departureIcao] : undefined
    return from ? [from.longitude, from.latitude] : null
  }, [departureIcao, coordsByIcao])

  const arrivalAt = useMemo<LonLat | null>(() => {
    const to = arrivalIcao ? coordsByIcao[arrivalIcao] : undefined
    return to ? [to.longitude, to.latitude] : null
  }, [arrivalIcao, coordsByIcao])

  const plannedPath = useMemo<LonLat[]>(() => {
    if (!departureAt || !arrivalAt) return []
    return sampleGreatCirclePath(departureAt[1], departureAt[0], arrivalAt[1], arrivalAt[0])
  }, [departureAt, arrivalAt])

  const flownPath = useMemo<LonLat[]>(
    () => (track?.points ?? []).map((point) => [point.lon, point.lat] as LonLat),
    [track],
  )

  const highestAltitudeFt = useMemo(() => {
    const altitudes = (track?.points ?? []).map((p) => p.altMslFt).filter((a): a is number => a !== null)
    return altitudes.length === 0 ? null : Math.max(...altitudes)
  }, [track])

  const body = () => {
    if (!flightId || status === 'idle' || status === 'loading') {
      return <Skeleton className="h-[280px] w-full rounded-lg" />
    }

    if (status === 'error') {
      return <p className="text-sm text-danger">Could not load this flight&rsquo;s track. Check your connection and try again.</p>
    }

    const points = track?.points ?? []
    const discarded = track?.discardedLeadingPointCount ?? 0

    if (points.length === 0) {
      return (
        <EmptyState
          icon={MapPinOff}
          title="No track was recorded for this flight"
          description="FSOps records a position every 15 seconds while it is tracking a flight in the simulator. This sector has none — either it was flown by one of your pilots rather than by you (there was no simulator attached to record from), or it predates position recording."
        />
      )
    }

    return (
      <div className="space-y-3">
        <Suspense fallback={<Skeleton className="h-[280px] w-full rounded-lg" />}>
          <FlownTrackMap
            flownPath={flownPath}
            plannedPath={plannedPath}
            departureIcao={departureIcao}
            arrivalIcao={arrivalIcao}
            departure={departureAt}
            arrival={arrivalAt}
            className="h-[280px] sm:h-[360px]"
          />
        </Suspense>

        {points.length === 1 && (
          <p className="text-sm text-muted-foreground">
            Only one position was ever recorded for this flight, so there is a point on the map but no path — the simulator
            disconnected almost immediately after tracking began.
          </p>
        )}

        <div className="flex flex-wrap items-center gap-x-5 gap-y-1 text-xs text-muted-foreground">
          <span className="flex items-center gap-1.5">
            <span aria-hidden className="inline-block h-0.5 w-6 rounded bg-accent" />
            Flown
          </span>
          <span className="flex items-center gap-1.5">
            <span
              aria-hidden
              className="inline-block h-0 w-6 rounded border-t-2 border-dashed border-muted-foreground"
            />
            Planned great circle
          </span>
          <span className="tabular-nums">
            {track!.recordedPointCount.toLocaleString('en-US')} position{track!.recordedPointCount === 1 ? '' : 's'} recorded, one
            roughly every 15 seconds
          </span>
          {highestAltitudeFt !== null && <span className="tabular-nums">Highest {fmt.altitude(highestAltitudeFt)}</span>}
        </div>

        {discarded > 0 && (
          <p className="text-xs text-muted-foreground">
            The first {discarded === 1 ? 'position was' : `${discarded.toLocaleString('en-US')} positions were`} recorded before
            the simulator had reported where the aircraft actually was, thousands of miles from {departureIcao ?? 'the departure airport'}
            , so {discarded === 1 ? 'it is' : 'they are'} not drawn. Nothing was deleted — {discarded === 1 ? 'the row is' : 'the rows are'}{' '}
            still counted in the {track!.recordedPointCount.toLocaleString('en-US')} above.
          </p>
        )}

        {track!.thinned && (
          <p className="text-xs text-muted-foreground">
            Drawn from {points.length.toLocaleString('en-US')} evenly-spaced points out of the{' '}
            {track!.recordedPointCount.toLocaleString('en-US')} recorded, purely to keep the map quick. The start and end are
            always kept, and nothing that was recorded has been changed or removed.
          </p>
        )}
      </div>
    )
  }

  return (
    <Card className={className}>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <RouteIcon className="size-4 text-muted-foreground" />
          Flown track
        </CardTitle>
        <p className="text-xs text-muted-foreground">
          Where the aircraft actually went, against the great circle the sector was planned along.
        </p>
      </CardHeader>
      <CardContent>{body()}</CardContent>
    </Card>
  )
}
