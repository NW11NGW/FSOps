import { useEffect, useRef, useState } from 'react'
import type { Feature, FeatureCollection, LineString } from 'geojson'
import { Map as MapLibreMap, Marker, NavigationControl } from 'maplibre-gl'
import type { GeoJSONSource, MapLayerMouseEvent } from 'maplibre-gl'

type MapLibreErrorEvent = { error?: { message?: string } }
import 'maplibre-gl/dist/maplibre-gl.css'

import { boundsForPath, splitAntimeridian } from '@/lib/geo'
import { cn } from '@/lib/utils'
import type { AirportSummary } from '@/types/airport'
import type { LonLat } from '@/types/route'

import { buildMapStyle, readMapColors, type MapColors, type MapThemeMode } from './mapTheme'

const ROUTE_SOURCE_ID = 'routes'
const ROUTE_GLOW_LAYER_ID = 'route-glow'
const ROUTE_LINE_LAYER_ID = 'route-line'
const ROUTE_HIT_LAYER_ID = 'route-hit'

/** Pseudo route id for the city pair currently being planned when it doesn't match a saved route. */
const PREVIEW_ROUTE_ID = '__preview__'

/** Screen-space radius (px) within which a dropped endpoint marker snaps onto another airport. */
const HIT_RADIUS_PX = 28

const EMPTY_ROUTE_GEOJSON: FeatureCollection<LineString, { routeId: string }> = { type: 'FeatureCollection', features: [] }

export interface SavedRouteArc {
  id: string
  departureIcao: string
  arrivalIcao: string
  path: LonLat[]
}

export interface NetworkAirport {
  icao: string
  latitude: number
  longitude: number
}

type MarkerRole = 'hub' | 'departure' | 'arrival' | 'network'
type EndpointRole = 'departure' | 'arrival'

interface RouteMapProps {
  departure: AirportSummary | null
  arrival: AirportSummary | null
  path: LonLat[]
  savedRoutes: SavedRouteArc[]
  networkAirports: NetworkAirport[]
  homeAirportIcao: string | null
  selectedRouteId: string | null
  hoveredRouteId: string | null
  onSelectRoute?: (routeId: string) => void
  onHoverRoute?: (routeId: string | null) => void
  onAirportClick?: (icao: string) => void
  onEndpointDrop?: (role: EndpointRole, icao: string) => void
  className?: string
}

interface MarkerSpec {
  icao: string
  lon: number
  lat: number
  role: MarkerRole
  isHub: boolean
  draggable: boolean
  title: string
}

interface MarkerEntry {
  marker: Marker
  role: MarkerRole
  draggable: boolean
}

interface Callbacks {
  onSelectRoute?: (routeId: string) => void
  onHoverRoute?: (routeId: string | null) => void
  onAirportClick?: (icao: string) => void
  onEndpointDrop?: (role: EndpointRole, icao: string) => void
}

function findMatchingSavedRoute(
  savedRoutes: SavedRouteArc[],
  departure: AirportSummary | null,
  arrival: AirportSummary | null,
): SavedRouteArc | undefined {
  if (!departure || !arrival) return undefined
  return savedRoutes.find(
    (route) =>
      (route.departureIcao === departure.icao && route.arrivalIcao === arrival.icao) ||
      (route.departureIcao === arrival.icao && route.arrivalIcao === departure.icao),
  )
}

/** Which route (saved id, or the unsaved-preview pseudo id) should render as "currently active". */
function resolveEmphasisId(
  savedRoutes: SavedRouteArc[],
  selectedRouteId: string | null,
  departure: AirportSummary | null,
  arrival: AirportSummary | null,
): string | null {
  if (selectedRouteId) return selectedRouteId
  const matching = findMatchingSavedRoute(savedRoutes, departure, arrival)
  if (matching) return matching.id
  if (departure && arrival) return PREVIEW_ROUTE_ID
  return null
}

function resolveFitPath(savedRoutes: SavedRouteArc[], emphasisId: string | null, previewPath: LonLat[]): LonLat[] {
  if (emphasisId && emphasisId !== PREVIEW_ROUTE_ID) {
    const saved = savedRoutes.find((route) => route.id === emphasisId)
    if (saved) return saved.path
  }
  return previewPath
}

function buildRouteFeatures(
  savedRoutes: SavedRouteArc[],
  emphasisId: string | null,
  previewPath: LonLat[],
): FeatureCollection<LineString, { routeId: string }> {
  const features: Feature<LineString, { routeId: string }>[] = []

  for (const route of savedRoutes) {
    for (const coordinates of splitAntimeridian(route.path)) {
      features.push({ type: 'Feature', properties: { routeId: route.id }, geometry: { type: 'LineString', coordinates } })
    }
  }

  if (emphasisId === PREVIEW_ROUTE_ID) {
    for (const coordinates of splitAntimeridian(previewPath)) {
      features.push({
        type: 'Feature',
        properties: { routeId: PREVIEW_ROUTE_ID },
        geometry: { type: 'LineString', coordinates },
      })
    }
  }

  return { type: 'FeatureCollection', features }
}

const ROUTE_LAYER_IDS = [ROUTE_GLOW_LAYER_ID, ROUTE_LINE_LAYER_ID, ROUTE_HIT_LAYER_ID] as const

/** Reads the `fsops:mapdebug` opt-in flag; wrapped in try/catch because `localStorage` can throw
 * in a locked-down embed or a privacy-restricted browser, and this must never break the map. */
function mapDebugEnabled(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem('fsops:mapdebug') != null
  } catch {
    return false
  }
}

function logMapDebug(map: MapLibreMap, context: string, featureCount: number): void {
  if (!mapDebugEnabled()) return
  // eslint-disable-next-line no-console
  // Layer order and the resolved paint values matter as much as presence: a line layer sitting
  // beneath the raster basemap, or painted with a colour MapLibre cannot parse, is invisible
  // while still reporting itself as present.
  let layerOrder: string[] = []
  let paint: Record<string, unknown> = {}
  let firstCoord: unknown = null
  try {
    layerOrder = map.getStyle().layers.map((layer) => layer.id)
    if (map.getLayer(ROUTE_LINE_LAYER_ID)) {
      paint = {
        color: map.getPaintProperty(ROUTE_LINE_LAYER_ID, 'line-color'),
        width: map.getPaintProperty(ROUTE_LINE_LAYER_ID, 'line-width'),
        opacity: map.getPaintProperty(ROUTE_LINE_LAYER_ID, 'line-opacity'),
      }
    }
    const source = map.getSource(ROUTE_SOURCE_ID) as
      | { _data?: { features?: { geometry?: { coordinates?: unknown[] } }[] } }
      | undefined
    firstCoord = source?._data?.features?.[0]?.geometry?.coordinates?.[0] ?? null
  } catch {
    // Diagnostics must never break the map.
  }

  // eslint-disable-next-line no-console
  console.debug('[fsops:map]', context, {
    sourcePresent: Boolean(map.getSource(ROUTE_SOURCE_ID)),
    glowLayerPresent: Boolean(map.getLayer(ROUTE_GLOW_LAYER_ID)),
    lineLayerPresent: Boolean(map.getLayer(ROUTE_LINE_LAYER_ID)),
    hitLayerPresent: Boolean(map.getLayer(ROUTE_HIT_LAYER_ID)),
    featureCount,
    layerOrder: layerOrder.join(' > '),
    linePaint: JSON.stringify(paint),
    firstCoord: JSON.stringify(firstCoord),
  })
}

function ensureRouteLayers(map: MapLibreMap, colors: MapColors): void {
  const sourcePresent = Boolean(map.getSource(ROUTE_SOURCE_ID))
  const allLayersPresent = sourcePresent && ROUTE_LAYER_IDS.every((id) => map.getLayer(id))
  if (allLayersPresent) return

  // Defensive re-assert: a style reload (`setStyle` diffing, an initial style transition, or any
  // other partial style mutation) can leave the source and its layers out of sync with each
  // other - e.g. the source surviving while a layer was dropped, or vice versa. Silently trusting
  // "the source exists, so we must be fine" is exactly what let the GeoJSON source sit there
  // fully populated with data but with no layer left to paint it. Tear down whatever fragment of
  // our own source/layers survived and rebuild the whole set from scratch every time anything is
  // missing, instead of no-opping.
  // Every one of these calls is wrapped, because during a style transition MapLibre's getSource /
  // getLayer can report nothing while the style still holds the entry internally. Trusting those
  // probes meant removeSource was skipped and the following addSource threw "Source 'routes'
  // already exists" - which aborted the whole sync before setData ever ran, so the layers existed,
  // the data was ready, and nothing was ever painted.
  for (const id of ROUTE_LAYER_IDS) {
    try {
      if (map.getLayer(id)) map.removeLayer(id)
    } catch {
      // Layer was already gone.
    }
  }
  try {
    if (map.getSource(ROUTE_SOURCE_ID)) map.removeSource(ROUTE_SOURCE_ID)
  } catch {
    // Source was already gone, or is still referenced; addSource below tolerates both.
  }

  try {
    map.addSource(ROUTE_SOURCE_ID, { type: 'geojson', data: EMPTY_ROUTE_GEOJSON, promoteId: 'routeId' })
  } catch {
    // The source survived the teardown - reuse it rather than losing the layers below.
  }

  // Base + glow layers are styled entirely from feature-state so every saved route can be drawn
  // in one source/pair-of-layers instead of restyling per selection - only the emphasised route
  // (selected, hovered, or the unsaved pair being planned) gets the glow and the widest/most-opaque
  // line. The rest of the network still renders in the accent hue (just thinner and more
  // translucent) so it reads clearly against both basemaps instead of disappearing - a muted-grey
  // hairline was previously indistinguishable from the dark CARTO tiles. No glow on unemphasised
  // routes keeps a dense network legible (no glow stacking).
  map.addLayer({
    id: ROUTE_GLOW_LAYER_ID,
    type: 'line',
    source: ROUTE_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      'line-color': colors.accentGlow,
      'line-width': 7,
      'line-blur': 4,
      'line-opacity': [
        'case',
        ['==', ['feature-state', 'emphasis'], 'selected'],
        0.55,
        ['==', ['feature-state', 'emphasis'], 'hovered'],
        0.35,
        0,
      ],
    },
  })
  map.addLayer({
    id: ROUTE_LINE_LAYER_ID,
    type: 'line',
    source: ROUTE_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      // Every route - emphasised or not - is drawn in the accent hue; only width/opacity change
      // with emphasis. A single shared colour reads as one legible network on both basemaps
      // instead of the unemphasised routes needing a second, weaker colour of their own.
      // Deliberately static values. Driving width/opacity from feature-state here left the whole
      // network unpainted; emphasis is carried by the separate glow layer instead, so the base
      // network always draws regardless of what feature state resolves to.
      'line-color': colors.accent,
      'line-width': 2,
      'line-opacity': 0.9,
    },
  })
  // Wide, invisible hit-target so thin/dim lines are still easy to hover and click.
  map.addLayer({
    id: ROUTE_HIT_LAYER_ID,
    type: 'line',
    source: ROUTE_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': colors.foreground, 'line-width': 16, 'line-opacity': 0 },
  })
}

function applyRouteColors(map: MapLibreMap, colors: MapColors): void {
  if (!map.getLayer(ROUTE_GLOW_LAYER_ID) || !map.getLayer(ROUTE_LINE_LAYER_ID)) return
  map.setPaintProperty(ROUTE_GLOW_LAYER_ID, 'line-color', colors.accentGlow)
  map.setPaintProperty(ROUTE_LINE_LAYER_ID, 'line-color', colors.accent)
}

function syncRouteData(
  map: MapLibreMap,
  colors: MapColors,
  savedRoutes: SavedRouteArc[],
  emphasisId: string | null,
  hoveredRouteId: string | null,
  previewPath: LonLat[],
  debugContext: string,
): void {
  ensureRouteLayers(map, colors)
  const source = map.getSource(ROUTE_SOURCE_ID) as GeoJSONSource | undefined
  if (!source) {
    // Should be unreachable now that ensureRouteLayers always (re)creates the source - logged so
    // it is visible with the debug flag on rather than failing silently again.
    logMapDebug(map, `${debugContext}: source missing after ensureRouteLayers`, 0)
    return
  }

  const featureCollection = buildRouteFeatures(savedRoutes, emphasisId, previewPath)
  source.setData(featureCollection)
  map.removeFeatureState({ source: ROUTE_SOURCE_ID })
  if (hoveredRouteId && hoveredRouteId !== emphasisId) {
    map.setFeatureState({ source: ROUTE_SOURCE_ID, id: hoveredRouteId }, { emphasis: 'hovered' })
  }
  if (emphasisId) {
    map.setFeatureState({ source: ROUTE_SOURCE_ID, id: emphasisId }, { emphasis: 'selected' })
  }
  logMapDebug(map, debugContext, featureCollection.features.length)
}

function nextClickIntent(departure: AirportSummary | null): EndpointRole {
  return !departure ? 'departure' : 'arrival'
}

function buildMarkerSpecs(
  networkAirports: NetworkAirport[],
  homeAirportIcao: string | null,
  departure: AirportSummary | null,
  arrival: AirportSummary | null,
): MarkerSpec[] {
  const specs: MarkerSpec[] = []
  const seen = new Set<string>()

  if (departure) {
    specs.push({
      icao: departure.icao,
      lon: departure.longitude,
      lat: departure.latitude,
      role: 'departure',
      isHub: departure.icao === homeAirportIcao,
      draggable: true,
      title: `Departure — ${departure.icao}. Drag onto another airport to reassign it.`,
    })
    seen.add(departure.icao)
  }
  if (arrival && arrival.icao !== departure?.icao) {
    specs.push({
      icao: arrival.icao,
      lon: arrival.longitude,
      lat: arrival.latitude,
      role: 'arrival',
      isHub: arrival.icao === homeAirportIcao,
      draggable: true,
      title: `Arrival — ${arrival.icao}. Drag onto another airport to reassign it.`,
    })
    seen.add(arrival.icao)
  }

  const intent = nextClickIntent(departure)
  const intentLabel = intent === 'departure' ? 'the departure' : 'the arrival'
  for (const airport of networkAirports) {
    if (seen.has(airport.icao)) continue
    seen.add(airport.icao)
    const isHub = airport.icao === homeAirportIcao
    specs.push({
      icao: airport.icao,
      lon: airport.longitude,
      lat: airport.latitude,
      role: isHub ? 'hub' : 'network',
      isHub,
      draggable: false,
      title: `${isHub ? 'Home base — ' : ''}Click to set ${airport.icao} as ${intentLabel}.`,
    })
  }
  return specs
}

function createMarkerElement(spec: MarkerSpec, onActivate: () => void): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = cn(
    'flex flex-col items-center gap-1 rounded-md outline-none',
    spec.draggable ? 'cursor-grab active:cursor-grabbing' : 'cursor-pointer',
    !spec.draggable && 'focus-visible:ring-2 focus-visible:ring-ring',
  )
  wrapper.dataset.icao = spec.icao
  wrapper.dataset.role = spec.role
  wrapper.title = spec.title

  if (!spec.draggable) {
    wrapper.setAttribute('role', 'button')
    wrapper.tabIndex = 0
    wrapper.setAttribute('aria-label', spec.title)
    wrapper.addEventListener('click', onActivate)
    wrapper.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault()
        onActivate()
      }
    })
  }

  // Every dot gets an accent-hued border so it reads against both the dark and light CARTO
  // basemaps - a plain `border-border` ring on a `bg-surface-elevated` fill is nearly the same
  // lightness as the tiles (and, in light mode, `surface-elevated` is pure white on pale tiles),
  // which was making destination markers disappear entirely. The hub is sized and ringed up
  // further so it reads as "home base" at a glance, distinct from ordinary destinations.
  const dot = document.createElement('div')
  const dotBase = 'rounded-full border-2 shadow-elevation-2 transition-transform'
  const dotVariant =
    spec.role === 'departure'
      ? 'size-3.5 border-background bg-accent'
      : spec.role === 'arrival'
        ? 'size-3.5 border-accent bg-background'
        : spec.role === 'hub'
          ? 'size-5 border-background bg-accent ring-4 ring-accent/30'
          : 'size-3 border-accent/70 bg-surface-elevated hover:scale-125 hover:border-accent'
  dot.className = cn(dotBase, dotVariant, spec.isHub && spec.role !== 'hub' && 'ring-4 ring-accent/30')
  wrapper.appendChild(dot)

  const label = document.createElement('div')
  label.className = cn(
    'rounded border px-1.5 py-0.5 font-mono text-[10px] font-semibold tracking-wide shadow-elevation-2',
    spec.role === 'network'
      ? 'border-accent/40 bg-surface-elevated text-foreground/90'
      : 'border-border bg-surface-elevated text-foreground',
  )
  label.textContent = spec.icao
  wrapper.appendChild(label)

  return wrapper
}

/**
 * Adds/updates/removes plain MapLibre Markers to match `specs`, keyed by ICAO. Markers whose role
 * hasn't changed are just repositioned (cheap); everything else is recreated so its element,
 * listeners and drag affordance stay in sync with its current role.
 */
function syncMarkers(
  map: MapLibreMap,
  registry: Map<string, MarkerEntry>,
  specs: MarkerSpec[],
  specsRef: { current: MarkerSpec[] },
  callbacksRef: { current: Callbacks },
): void {
  specsRef.current = specs
  const nextIcaos = new Set(specs.map((spec) => spec.icao))

  for (const [icao, entry] of registry) {
    if (!nextIcaos.has(icao)) {
      entry.marker.remove()
      registry.delete(icao)
    }
  }

  for (const spec of specs) {
    const existing = registry.get(spec.icao)
    if (existing && existing.role === spec.role && existing.draggable === spec.draggable) {
      existing.marker.setLngLat([spec.lon, spec.lat])
      const element = existing.marker.getElement()
      element.title = spec.title
      element.setAttribute('aria-label', spec.title)
      continue
    }

    existing?.marker.remove()

    const onActivate = () => callbacksRef.current.onAirportClick?.(spec.icao)
    const element = createMarkerElement(spec, onActivate)
    const marker = new Marker({ element, anchor: 'bottom', draggable: spec.draggable }).setLngLat([spec.lon, spec.lat]).addTo(map)

    if (spec.draggable) {
      const role: EndpointRole = spec.role === 'departure' ? 'departure' : 'arrival'
      const icaoAtCreation = spec.icao
      marker.on('dragstart', () => element.classList.add('opacity-70'))
      marker.on('dragend', () => {
        element.classList.remove('opacity-70')
        const point = map.project(marker.getLngLat())
        let closestIcao: string | null = null
        let closestDist = HIT_RADIUS_PX
        for (const other of specsRef.current) {
          if (other.icao === icaoAtCreation) continue
          const otherPoint = map.project([other.lon, other.lat])
          const dist = Math.hypot(otherPoint.x - point.x, otherPoint.y - point.y)
          if (dist <= closestDist) {
            closestDist = dist
            closestIcao = other.icao
          }
        }
        if (closestIcao) {
          callbacksRef.current.onEndpointDrop?.(role, closestIcao)
        } else {
          // No airport nearby - snap back to wherever this endpoint currently is.
          const current = specsRef.current.find((s) => s.icao === icaoAtCreation && s.role === role)
          if (current) marker.setLngLat([current.lon, current.lat])
        }
      })
    }

    registry.set(spec.icao, { marker, role: spec.role, draggable: spec.draggable })
  }
}

function moveCamera(
  map: MapLibreMap,
  departure: AirportSummary | null,
  arrival: AirportSummary | null,
  path: LonLat[],
  networkAirports: NetworkAirport[],
  animate: boolean,
): void {
  const duration = (ms: number) => (animate ? ms : 0)

  if (departure && !arrival) {
    map.flyTo({ center: [departure.longitude, departure.latitude], zoom: Math.max(map.getZoom(), 5), duration: duration(1100), essential: true })
    return
  }

  const points: LonLat[] = path.length > 1 ? path : []
  if (points.length === 0) {
    if (departure) points.push([departure.longitude, departure.latitude])
    if (arrival) points.push([arrival.longitude, arrival.latitude])
  }

  if (points.length === 0) {
    // Nothing picked - show the whole network as an overview if there is one to show.
    const networkPoints: LonLat[] = networkAirports.map((airport) => [airport.longitude, airport.latitude])
    if (networkPoints.length === 0) return
    const bounds = boundsForPath(networkPoints)
    if (!bounds) return
    map.fitBounds(bounds, { padding: { top: 64, bottom: 64, left: 64, right: 64 }, duration: duration(900), maxZoom: 7, essential: true })
    return
  }

  if (points.length === 1) {
    map.flyTo({ center: points[0], zoom: Math.max(map.getZoom(), 5), duration: duration(1100), essential: true })
    return
  }

  const bounds = boundsForPath(points)
  if (!bounds) return
  map.fitBounds(bounds, { padding: { top: 56, bottom: 56, left: 56, right: 56 }, duration: duration(900), maxZoom: 9, essential: true })
}

/**
 * Dark/light MapLibre basemap (CARTO raster tiles) drawing every saved route as a great-circle
 * arc (the "spider web"), plus the airline's hub and destination markers. The route currently
 * being planned or selected in the table is emphasised; the rest stay dim but visible. Tiles
 * require internet; if they fail to load the themed background layer plus routes/markers still
 * render normally (see mapTheme.ts).
 */
export function RouteMap({
  departure,
  arrival,
  path,
  savedRoutes,
  networkAirports,
  homeAirportIcao,
  selectedRouteId,
  hoveredRouteId,
  onSelectRoute,
  onHoverRoute,
  onAirportClick,
  onEndpointDrop,
  className,
}: RouteMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const markersRef = useRef<Map<string, MarkerEntry>>(new Map())
  const specsRef = useRef<MarkerSpec[]>([])
  const modeRef = useRef<MapThemeMode>('dark')
  const cameraKeyRef = useRef<string>('')
  const latestRef = useRef({ departure, arrival, path, savedRoutes, networkAirports, homeAirportIcao, selectedRouteId, hoveredRouteId })
  const callbacksRef = useRef<Callbacks>({ onSelectRoute, onHoverRoute, onAirportClick, onEndpointDrop })
  // Tracks readiness independently of MapLibre's own isStyleLoaded()/loaded(), which for a
  // raster source stays false until its tiles have actually downloaded - offline, that never
  // happens, so gating on it would mean routes and markers never appear without internet.
  // 'style.load' fires as soon as the style JSON + source definitions are parsed, before any
  // tile request is made, which is the point sources/layers become safe to add.
  const styleReadyRef = useRef(false)
  // Whether the map has completed at least one 'style.load'. Guards against calling `setStyle`
  // before the *first* style has finished its own (inherently async) load - see the mount effect.
  const hasLoadedOnceRef = useRef(false)
  const [tilesUnavailable, setTilesUnavailable] = useState(false)

  latestRef.current = { departure, arrival, path, savedRoutes, networkAirports, homeAirportIcao, selectedRouteId, hoveredRouteId }
  callbacksRef.current = { onSelectRoute, onHoverRoute, onAirportClick, onEndpointDrop }

  // When the map debug flag is set, publish the live map so it can be interrogated from the
  // console (layer order, resolved paint, queryRenderedFeatures). Off by default.
  if (mapDebugEnabled() && typeof window !== 'undefined') {
    ;(window as unknown as { __fsopsMap?: MapLibreMap | null }).__fsopsMap = mapRef.current
  }

  useEffect(() => {
    const container = containerRef.current
    if (!container) return undefined

    const initialMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
    modeRef.current = initialMode

    const map = new MapLibreMap({
      container,
      style: buildMapStyle(initialMode, readMapColors()),
      center: [10, 25],
      zoom: 1.2,
      attributionControl: { compact: true },
    })
    mapRef.current = map
    map.addControl(new NavigationControl({ showCompass: false }), 'top-right')

    map.on('error', (_event: MapLibreErrorEvent) => {
      // Tile/style load failures (typically no internet) must never throw or spam the console -
      // an unhandled 'error' listener is exactly what makes MapLibre print to console itself.
      setTilesUnavailable(true)
    })

    const fullSync = (animate: boolean) => {
      const current = mapRef.current
      if (!current) return
      const { departure: dep, arrival: arr, path: currentPath, savedRoutes: routes, networkAirports: network, homeAirportIcao: hub, selectedRouteId: selected, hoveredRouteId: hovered } =
        latestRef.current
      const emphasisId = resolveEmphasisId(routes, selected, dep, arr)
      syncRouteData(current, readMapColors(), routes, emphasisId, hovered, currentPath, 'full-sync')
      syncMarkers(current, markersRef.current, buildMarkerSpecs(network, hub, dep, arr), specsRef, callbacksRef)
      moveCamera(current, dep, arr, resolveFitPath(routes, emphasisId, currentPath), network, animate)
      cameraKeyRef.current = `${dep?.icao ?? ''}|${arr?.icao ?? ''}|${emphasisId ?? ''}`
    }

    // Swaps the basemap style for a theme change. Only ever called once the map's *current* style
    // has itself finished loading (see callers below) - calling `setStyle` while a style is still
    // mid-load is a known-fragile MapLibre sequence and previously left the route source/layers
    // out of sync with whichever style ultimately settled, which is why the arcs could silently
    // fail to (re)attach after the very first paint.
    const swapStyle = (nextMode: MapThemeMode) => {
      modeRef.current = nextMode
      styleReadyRef.current = false
      map.once('style.load', () => {
        styleReadyRef.current = true
        fullSync(false)
      })
      map.setStyle(buildMapStyle(nextMode, readMapColors()))
    }

    map.on('style.load', () => {
      if (!hasLoadedOnceRef.current) {
        hasLoadedOnceRef.current = true
        // The DOM's actual theme class can briefly disagree with `initialMode`: `useTheme`
        // applies the `dark` class in its own effect, and React fires a child component's
        // passive effects (this one) before an ancestor's (the theme provider's) in the same
        // commit - so `initialMode`, sampled synchronously above, can read the wrong theme on
        // every fresh mount. Re-check now that the first style has genuinely finished loading,
        // and self-correct via a single clean `setStyle` instead of leaving it to the
        // MutationObserver below, which would otherwise fire this same correction while the
        // first style is still loading.
        const actualMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
        if (actualMode !== modeRef.current) {
          swapStyle(actualMode)
          return
        }
      }
      styleReadyRef.current = true
      fullSync(false)
    })

    let hoveredHitId: string | null = null
    map.on('mousemove', ROUTE_HIT_LAYER_ID, (event: MapLayerMouseEvent) => {
      const routeId = event.features?.[0]?.properties?.routeId as string | undefined
      map.getCanvas().style.cursor = 'pointer'
      if (routeId && routeId !== PREVIEW_ROUTE_ID && routeId !== hoveredHitId) {
        hoveredHitId = routeId
        callbacksRef.current.onHoverRoute?.(routeId)
      }
    })
    map.on('mouseleave', ROUTE_HIT_LAYER_ID, () => {
      map.getCanvas().style.cursor = ''
      if (hoveredHitId) {
        hoveredHitId = null
        callbacksRef.current.onHoverRoute?.(null)
      }
    })
    map.on('click', ROUTE_HIT_LAYER_ID, (event: MapLayerMouseEvent) => {
      const routeId = event.features?.[0]?.properties?.routeId as string | undefined
      if (routeId && routeId !== PREVIEW_ROUTE_ID) {
        callbacksRef.current.onSelectRoute?.(routeId)
      }
    })

    const observer = new MutationObserver(() => {
      const current = mapRef.current
      if (!current) return

      const nextMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
      if (nextMode === modeRef.current) {
        applyRouteColors(current, readMapColors())
        return
      }
      if (!hasLoadedOnceRef.current) {
        // The map's first style hasn't finished loading yet - the 'style.load' handler above
        // will self-correct the theme once it has, rather than racing a `setStyle` call against
        // it here.
        return
      }
      swapStyle(nextMode)
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] })

    // Captured in the effect body rather than read as `markersRef.current` in the cleanup below:
    // React runs cleanup after a ref may already point somewhere else, and although this one holds
    // a single Map created at mount and never reassigned, capturing it makes that provable rather
    // than something the next reader has to verify by inspection.
    const markers = markersRef.current

    return () => {
      observer.disconnect()
      for (const entry of markers.values()) entry.marker.remove()
      markers.clear()
      map.remove()
      mapRef.current = null
    }
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return

    const emphasisId = resolveEmphasisId(savedRoutes, selectedRouteId, departure, arrival)
    syncRouteData(map, readMapColors(), savedRoutes, emphasisId, hoveredRouteId, path, 'props-sync')
    syncMarkers(map, markersRef.current, buildMarkerSpecs(networkAirports, homeAirportIcao, departure, arrival), specsRef, callbacksRef)

    // Only move the camera when what the user is actively planning/selecting changes - hovering a
    // table row or arc must highlight it without yanking the viewport around.
    const cameraKey = `${departure?.icao ?? ''}|${arrival?.icao ?? ''}|${emphasisId ?? ''}`
    if (cameraKeyRef.current !== cameraKey) {
      cameraKeyRef.current = cameraKey
      moveCamera(map, departure, arrival, resolveFitPath(savedRoutes, emphasisId, path), networkAirports, true)
    }
  }, [departure, arrival, path, savedRoutes, networkAirports, homeAirportIcao, selectedRouteId, hoveredRouteId])

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[240px]" role="application" aria-label="Route network map" />
      {tilesUnavailable && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
          Map tiles unavailable — showing offline view
        </div>
      )}
    </div>
  )
}
