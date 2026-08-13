/**
 * GET /api/v1/operations/live - backs the dashboard's live operations map, which answers "what is
 * my airline doing right now" at a glance. `player` aircraft are the pilot's own tracked flight, driven by real
 * telemetry; `virtual` aircraft are other pilots' scheduled occurrences, their position
 * interpolated server-side from the route's great circle and elapsed block time - nothing here
 * is stored, so it stays consistent with the deterministic wall-clock catch-up model.
 *
 * `aircraftType` and `headingDeg` are typed as nullable/optional even though the backend
 * populates both for real flights now (OperationsEndpoints.cs joins AircraftTypes for both kinds,
 * and `player` heading comes from LiveFlightSnapshot.TrueHeadingDeg): `aircraftType` can still be
 * null if a flight's FleetAircraft record didn't resolve, so the UI must keep degrading
 * gracefully rather than assuming presence.
 */
export type LiveAircraftKind = 'player' | 'virtual'

export interface LiveAircraft {
  kind: LiveAircraftKind
  flightId: string | null
  pilotName: string
  registration: string | null
  aircraftType?: string | null
  routeId: string | null
  flightNumber: string | null
  departureIcao: string | null
  arrivalIcao: string | null
  latitudeDeg: number
  longitudeDeg: number
  headingDeg: number | null
  phase: string
  percentComplete: number | null
  departureUtc: string
  estimatedArrivalUtc: string
  elapsedMinutes: number
  remainingMinutes: number
}

export interface LiveNetworkRoute {
  routeId: string
  departureIcao: string
  departureLat: number
  departureLon: number
  arrivalIcao: string
  arrivalLat: number
  arrivalLon: number
  distanceNm: number
}

export interface LiveOperationsResponse {
  aircraft: LiveAircraft[]
  network: LiveNetworkRoute[]
}

/**
 * GET /api/v1/operations/atc - backs the dashboard's ATC layer. Deliberately narrower than the
 * fuller VATSIM integration once envisaged (which also covered other people's live traffic and
 * verifying a flight was genuinely flown online): this is online controllers only, and only ones
 * covering an airport in the airline's own route network - never a global controller list, and
 * never other pilots' traffic.
 *
 * `status` distinguishes "the feed answered but nobody relevant is online" (`'ok'` with an empty
 * list) from "the feed itself could not be read" (`'unavailable'`) - the UI must tell these apart
 * rather than showing the same empty state for both.
 */
export type VatsimAtcStatus = 'ok' | 'unavailable'

/**
 * How much FSOps actually knows about what a controller is working - and therefore how it may
 * honestly be drawn. The two are drawn differently on purpose, and the map legend says so, because
 * on a map a guess and a fact look equally authoritative.
 *
 * `'terminal'` - tower, ground, delivery, airport-named approach. Genuinely airport-local, so
 * `latitudeDeg`/`longitudeDeg` are the airport and `visualRangeNm` may be drawn as a circle around
 * it. That circle is an **approximation**: it is the range the controller's own client is set to
 * see, not the shape of anything they control.
 *
 * `'sector'` - centre and flight service. Real published FIR/UIR geometry, referenced by
 * `boundaryId` into {@link VatsimAtcResponse.boundaries}. There is deliberately **no position**:
 * a sector is an area, and a marker would put a pin where nobody is.
 *
 * There is no third kind, and that is the point. A controller FSOps cannot place - a TRACON like
 * `NY_APP`, or any callsign missing from the bundled boundary data - never reaches the client at
 * all. Silence is the honest representation of "we don't know"; a plausible circle is not.
 */
export type VatsimAtcCoverageKind = 'terminal' | 'sector'

export interface VatsimAtcController {
  callsign: string
  facilityLabel: string
  frequency: string
  coverageKind: VatsimAtcCoverageKind
  /** Set for `'terminal'` coverage only. Null for a sector, which has no single airport. */
  airportIcao: string | null
  airportName: string | null
  /** Null for `'sector'` coverage - see {@link VatsimAtcCoverageKind}. The map keys its controller
   *  marker layer off these being non-null, so a sector correctly gets no marker. */
  latitudeDeg: number | null
  longitudeDeg: number | null
  /** The feed's own `visual_range`. Only meaningful for `'terminal'` coverage, and only ever as an
   *  approximation. */
  visualRangeNm: number | null
  /** Set for `'sector'` coverage only: the key into {@link VatsimAtcResponse.boundaries}, and the
   *  reason two controllers splitting one region share one polygon rather than drawing two. */
  boundaryId: string | null
  boundaryName: string | null
  /** Whether this position covers an airport the airline actually serves - the airport itself for
   *  terminal coverage, or a boundary containing one for a sector.
   *
   *  This used to be the filter, and is now emphasis: the dashboard list follows the map's
   *  viewport, so hiding a controller outside the network would contradict the shape the user can
   *  see on screen. Under the default network scope it is always true. */
  inNetwork: boolean
  logonTimeUtc: string
}

export interface VatsimAtcResponse {
  status: VatsimAtcStatus
  fetchedAtUtc: string | null
  controllers: VatsimAtcController[]
  /** Boundary id -> GeoJSON MultiPolygon `coordinates` (polygon -> ring -> position -> [lon, lat]),
   *  sent once per boundary rather than inlined per controller.
   *
   *  Null unless the request opted in with `?geometry=true`: the controller list wants the same
   *  controller data without coordinates it would throw away, since it draws no boundaries. Also
   *  null when nothing online has a boundary.
   *
   *  Lateral footprint only. The source data carries no vertical extent, so nothing here says
   *  anything about altitude bands. */
  boundaries: Record<string, number[][][][]> | null
}

/**
 * GET /vatsim/traffic - G11: other VATSIM pilots' live traffic near the airline's own network,
 * for the live operations map. Deliberately thin (no CID, no pilot name) - this is "other people's
 * traffic" for a subordinate map marker, never a profile. Off by default - see
 * `vatsimTrafficVisibility` for why the stored preference must never quietly resolve to on.
 */
export type VatsimTrafficStatus = 'ok' | 'unavailable'

export interface VatsimTrafficPilot {
  callsign: string
  latitudeDeg: number
  longitudeDeg: number
  altitudeFt: number
  groundSpeedKt: number
  headingDeg: number
  departureIcao: string | null
  arrivalIcao: string | null
}

export interface VatsimTrafficResponse {
  status: VatsimTrafficStatus
  fetchedAtUtc: string | null
  pilots: VatsimTrafficPilot[]
}

/**
 * GET /vatsim/history - G9: FSOps' OWN record of which of the airline's flights were corroborated
 * as genuinely flown online on VATSIM (see `Flight.vatsimOnline`/G8). This is deliberately NOT a
 * call to VATSIM's own history - the public feed has no history at all, and VATSIM's Core API
 * history endpoint is key-gated (reading any CID's past sessions is a real privacy surface VATSIM
 * doesn't hand out keylessly). Present this plainly as FSOps' own record - see the component that
 * renders it.
 */
export interface VatsimHistoryEntry {
  flightId: string
  routeId: string
  departureIcao: string | null
  arrivalIcao: string | null
  flightNumber: string | null
  completedUtc: string
  online: boolean
  /** Fraction (0-1) of the corroboration checks that matched while this flight was tracked. */
  onlineFraction: number | null
  /** The callsign the configured CID was flying under when last matched. */
  callsign: string | null
  controllersWorked: string[]
}

export interface VatsimHistoryResponse {
  /** False means no VATSIM CID is set in Settings at all - a different, more actionable empty
   *  state than "a CID is set but nothing has been corroborated yet". */
  cidConfigured: boolean
  flights: VatsimHistoryEntry[]
}
