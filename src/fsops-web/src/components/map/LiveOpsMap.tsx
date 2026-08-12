import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { Feature, FeatureCollection, LineString } from 'geojson'
import { Map as MapLibreMap, Marker, NavigationControl } from 'maplibre-gl'
import type { GeoJSONSource } from 'maplibre-gl'
import { PlaneTakeoff, Route as RouteIcon, Users } from 'lucide-react'

type MapLibreErrorEvent = { error?: { message?: string } }
import 'maplibre-gl/dist/maplibre-gl.css'

import { buildMapStyle, readMapColors, type MapThemeMode } from '@/components/map/mapTheme'
import { LiveFlightCard } from '@/components/map/LiveFlightCard'
import { AtcControllerCard } from '@/components/map/AtcControllerCard'
import { AtcSectorCard } from '@/components/map/AtcSectorCard'
import { AtcMapLegend } from '@/components/map/AtcMapLegend'
import { buildAtcSectorFeatures, buildAtcTerminalFeatures, controllerKeyFor } from '@/components/map/atcGeometry'
import type { MapBounds } from '@/components/map/atcVisibility'
import { Button } from '@/components/ui/button'
import { boundsForPath, sampleGreatCirclePath, splitAntimeridian } from '@/lib/geo'
import { cn } from '@/lib/utils'
import type { LonLat } from '@/types/route'
import type { LiveAircraft, LiveNetworkRoute, VatsimAtcController, VatsimAtcResponse } from '@/types/operations'
import type { MultiPolygon, Polygon } from 'geojson'

const NETWORK_SOURCE_ID = 'live-ops-network'
const NETWORK_LAYER_ID = 'live-ops-network-line'
const EMPTY_NETWORK_GEOJSON: FeatureCollection<LineString> = { type: 'FeatureCollection', features: [] }

/** Real published FIR/UIR geometry: solid outline, stronger fill. */
const ATC_SECTOR_SOURCE_ID = 'live-ops-atc-sectors'
const ATC_SECTOR_FILL_LAYER_ID = 'live-ops-atc-sectors-fill'
const ATC_SECTOR_LINE_LAYER_ID = 'live-ops-atc-sectors-line'
const EMPTY_ATC_SECTOR_GEOJSON: FeatureCollection<MultiPolygon> = { type: 'FeatureCollection', features: [] }

/** Approximate range circles around airport-local positions: dashed outline, weaker fill, so a
 *  sketch never carries the same visual weight as a published boundary. The legend names the
 *  difference; this makes it readable before anyone consults the legend. */
const ATC_TERMINAL_SOURCE_ID = 'live-ops-atc-terminal'
const ATC_TERMINAL_FILL_LAYER_ID = 'live-ops-atc-terminal-fill'
const ATC_TERMINAL_LINE_LAYER_ID = 'live-ops-atc-terminal-line'
const EMPTY_ATC_TERMINAL_GEOJSON: FeatureCollection<Polygon> = { type: 'FeatureCollection', features: [] }

/** Required by CC BY-SA 4.0, and attached to the sector source rather than the basemap style so it
 *  appears only on a map that is actually drawing this data - RouteMap and LiveFlightMap draw no
 *  boundaries and must not credit a source they do not use. */
const VATSPY_ATTRIBUTION =
  'FIR boundaries &copy; <a href="https://github.com/vatsimnetwork/vatspy-data-project" target="_blank" rel="noreferrer">VAT-Spy Data Project</a>, <a href="https://creativecommons.org/licenses/by-sa/4.0/" target="_blank" rel="noreferrer">CC BY-SA 4.0</a>'

/** Coalesces a pan or zoom gesture's tail (inertia, a wheel burst) into one viewport update. The
 *  list re-filters from data already in hand, so this only exists to avoid re-running the geometry
 *  a dozen times per flick - it never gates, delays or triggers a fetch. */
const VIEWPORT_DEBOUNCE_MS = 150

const RADIO_TOWER_SVG =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="14" height="14"><path d="M4.9 16.1C1 20 1 20 1 20"/><path d="M7.8 13.2c-3 3-3 3-3 3"/><circle cx="12" cy="9" r="2"/><path d="M12 11v11"/><path d="M9 22h6"/><path d="M15.2 13.2c3 3 3 3 3 3"/><path d="M19.1 16.1c3.9 3.9 3.9 3.9 3.9 3.9"/></svg>'

function controllerMarkerElement(label: string): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className =
    'flex size-6 items-center justify-center rounded-full border-2 border-warning bg-surface-elevated text-warning shadow-elevation-2 ring-2 ring-background'
  wrapper.innerHTML = RADIO_TOWER_SVG
  wrapper.setAttribute('role', 'img')
  wrapper.setAttribute('aria-label', label)
  return wrapper
}

/** Smooth interpolated-position transition duration. Positions update on a gentle interval with
 *  smooth transitions rather than jumping. Aircraft refresh about once a minute, so this
 *  only needs to be long enough to read as motion rather than a jump, not to match real speed. */
const MARKER_TRANSITION_MS = 1500

interface LiveOpsMapProps {
  aircraft: LiveAircraft[]
  network: LiveNetworkRoute[]
  /** Online VATSIM controllers FSOps can place. Omit (or
   *  pass an empty array) when the ATC layer has nothing to show; use `atcUnavailable` to
   *  distinguish "nothing online" from "couldn't reach the feed". */
  atcControllers?: VatsimAtcController[]
  /** Published boundary geometry, keyed by boundary id, as sent by `/operations/atc?geometry=true`.
   *  Sector controllers whose boundary is absent here draw nothing at all rather than a guess. */
  atcBoundaries?: VatsimAtcResponse['boundaries']
  atcUnavailable?: boolean
  /** Reports the visible map bounds after every pan and zoom, debounced, so the controller list
   *  beside the map can show what is genuinely on screen. Called with the initial view on load.
   *  A consumer without a map simply never receives one, which is the panel's case. */
  onViewportChange?: (bounds: MapBounds) => void
  className?: string
}

interface AircraftMarkerEntry {
  marker: Marker
  kind: LiveAircraft['kind']
  animationFrame: number | null
}

/** Stable identity for an aircraft entry across polls. Player flights have a real flightId;
 *  virtual occurrences don't persist anything, so fall back to a composite of fields that
 *  together identify one scheduled occurrence. */
function keyFor(ac: LiveAircraft): string {
  if (ac.flightId) return ac.flightId
  return `virtual:${ac.routeId ?? ''}:${ac.registration ?? ''}:${ac.flightNumber ?? ''}`
}

const PLANE_SVG =
  '<svg viewBox="0 0 24 24" fill="currentColor" width="18" height="18"><path d="M22 16.5v-2l-8.5-5V4.5c0-1-.8-2-1.5-2s-1.5 1-1.5 2v5l-8.5 5v2l8.5-2.5V19l-2.5 1.8V22l4-1 4 1v-1.2L13.5 19v-5.5z"/></svg>'

/**
 * Player and virtual aircraft render with deliberately different weight: the player's own flight
 * is real telemetry and gets the solid, full-opacity accent treatment used everywhere else live
 * data appears; virtual (interpolated) traffic gets a lighter, outlined treatment so it visually
 * reads as "other/simulated" traffic at a glance, matching the visual language other people's live
 * VATSIM traffic would use if it is ever drawn here.
 */
function aircraftMarkerElement(kind: LiveAircraft['kind'], label: string): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = cn(
    'flex items-center justify-center rounded-full shadow-elevation-2 ring-2 ring-background',
    kind === 'player'
      ? 'size-8 bg-accent text-accent-foreground shadow-elevation-3'
      : 'size-6 border-2 border-accent/70 bg-surface-elevated text-accent opacity-90',
  )
  wrapper.innerHTML = PLANE_SVG
  wrapper.style.cursor = kind === 'player' ? 'pointer' : 'default'
  if (kind === 'player') {
    wrapper.setAttribute('role', 'button')
    wrapper.tabIndex = 0
  } else {
    wrapper.setAttribute('role', 'img')
  }
  wrapper.setAttribute('aria-label', label)
  return wrapper
}

function airportDotElement(icao: string): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex flex-col items-center gap-1'
  wrapper.setAttribute('aria-hidden', 'true')
  const dot = document.createElement('div')
  dot.className = 'size-2 rounded-full border-2 border-accent/70 bg-surface-elevated shadow-elevation-2'
  wrapper.appendChild(dot)
  const tag = document.createElement('div')
  tag.className =
    'rounded border border-border bg-surface-elevated/90 px-1 py-0.5 font-mono text-[9px] font-semibold text-muted-foreground shadow-elevation-2'
  tag.textContent = icao
  wrapper.appendChild(tag)
  return wrapper
}

function buildNetworkFeatures(network: LiveNetworkRoute[]): FeatureCollection<LineString> {
  const features: Feature<LineString>[] = []
  for (const route of network) {
    const path = sampleGreatCirclePath(route.departureLat, route.departureLon, route.arrivalLat, route.arrivalLon)
    for (const coordinates of splitAntimeridian(path)) {
      features.push({ type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates } })
    }
  }
  return { type: 'FeatureCollection', features }
}

function networkAirports(network: LiveNetworkRoute[]): { icao: string; lon: number; lat: number }[] {
  const seen = new Map<string, { icao: string; lon: number; lat: number }>()
  for (const route of network) {
    if (!seen.has(route.departureIcao)) {
      seen.set(route.departureIcao, { icao: route.departureIcao, lon: route.departureLon, lat: route.departureLat })
    }
    if (!seen.has(route.arrivalIcao)) {
      seen.set(route.arrivalIcao, { icao: route.arrivalIcao, lon: route.arrivalLon, lat: route.arrivalLat })
    }
  }
  return Array.from(seen.values())
}

/** Reads the `fsops:mapdebug` opt-in flag - same convention as RouteMap.tsx - wrapped in
 *  try/catch because `localStorage` can throw in locked-down embeds (e.g. the in-sim panel
 *  webview) and this must never break the map. */
function mapDebugEnabled(): boolean {
  try {
    return typeof window !== 'undefined' && window.localStorage.getItem('fsops:mapdebug') != null
  } catch {
    return false
  }
}

function shortestHeadingDelta(from: number, to: number): number {
  return (((to - from + 180) % 360) + 360) % 360 - 180
}

/**
 * Animates a Marker from its current position/rotation to a new one instead of snapping, so a
 * refreshed poll reads as gentle motion rather than a jump. Cancels any in-flight animation for
 * the same marker before starting a new one (a poll landing mid-transition must not stack tweens).
 */
function animateMarker(entry: AircraftMarkerEntry, toLng: number, toLat: number, toHeading: number): void {
  if (entry.animationFrame !== null) cancelAnimationFrame(entry.animationFrame)

  const from = entry.marker.getLngLat()
  const fromLng = from.lng
  const fromLat = from.lat
  const fromHeading = entry.marker.getRotation()
  const headingDelta = shortestHeadingDelta(fromHeading, toHeading)
  const start = performance.now()

  const step = (now: number) => {
    const t = Math.min(1, (now - start) / MARKER_TRANSITION_MS)
    const eased = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2
    entry.marker.setLngLat([fromLng + (toLng - fromLng) * eased, fromLat + (toLat - fromLat) * eased])
    entry.marker.setRotation(fromHeading + headingDelta * eased)
    entry.animationFrame = t < 1 ? requestAnimationFrame(step) : null
  }
  entry.animationFrame = requestAnimationFrame(step)
}

/**
 * Dashboard's "what is my airline doing right now" map: every created route as a great-circle
 * arc plus every aircraft currently airborne (the player's own tracked flight, real telemetry;
 * every virtual pilot's currently-in-progress occurrence, interpolated). Shares the CARTO
 * basemap/theming helpers with RouteMap and LiveFlightMap for visual consistency, but is its own
 * component - it has different data shape (a whole fleet of moving markers, refreshed on a poll,
 * with smooth transitions) and a designed empty state neither of the other two needs.
 */
export function LiveOpsMap({
  aircraft,
  network,
  atcControllers = [],
  atcBoundaries = null,
  atcUnavailable = false,
  onViewportChange,
  className,
}: LiveOpsMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const markersRef = useRef<Map<string, AircraftMarkerEntry>>(new Map())
  const airportMarkersRef = useRef<Map<string, Marker>>(new Map())
  const controllerMarkersRef = useRef<Map<string, Marker>>(new Map())
  const modeRef = useRef<MapThemeMode>('dark')
  const styleReadyRef = useRef(false)
  const firedInitialFitRef = useRef(false)
  const latestRef = useRef({ aircraft, network, atcControllers, atcBoundaries })
  const navigate = useNavigate()
  const navigateRef = useRef(navigate)
  const onViewportChangeRef = useRef(onViewportChange)
  const [tilesUnavailable, setTilesUnavailable] = useState(false)
  const [hoveredKey, setHoveredKey] = useState<string | null>(null)
  const [hoverPos, setHoverPos] = useState<{ x: number; y: number } | null>(null)
  const [hoveredControllerKey, setHoveredControllerKey] = useState<string | null>(null)
  const [hoveredControllerPos, setHoveredControllerPos] = useState<{ x: number; y: number } | null>(null)
  const [hoveredSectorId, setHoveredSectorId] = useState<string | null>(null)
  const [hoveredSectorPos, setHoveredSectorPos] = useState<{ x: number; y: number } | null>(null)
  // Mirrors hoveredKey/hoveredControllerKey for the mount-only effect's closures (sync/move
  // handler), which are created once and would otherwise only ever see the initial (null) state.
  const hoveredKeyRef = useRef<string | null>(null)
  const hoveredControllerKeyRef = useRef<string | null>(null)

  latestRef.current = { aircraft, network, atcControllers, atcBoundaries }
  navigateRef.current = navigate
  onViewportChangeRef.current = onViewportChange
  hoveredKeyRef.current = hoveredKey
  hoveredControllerKeyRef.current = hoveredControllerKey

  // Publishes the live map for console interrogation (queryRenderedFeatures etc.) when the debug
  // flag is set - same convention as RouteMap.tsx. Off by default.
  if (mapDebugEnabled() && typeof window !== 'undefined') {
    ;(window as unknown as { __fsopsLiveOpsMap?: MapLibreMap | null }).__fsopsLiveOpsMap = mapRef.current
  }

  const hoveredAircraft = hoveredKey ? aircraft.find((ac) => keyFor(ac) === hoveredKey) ?? null : null
  const hoveredController = hoveredControllerKey
    ? atcControllers.find((c) => controllerKeyFor(c) === hoveredControllerKey) ?? null
    : null

  // The hovered aircraft can vanish from a poll (landed, occurrence ended) - drop the stale card
  // rather than leaving it pinned to a marker that no longer exists.
  useEffect(() => {
    if (hoveredKey && !aircraft.some((ac) => keyFor(ac) === hoveredKey)) {
      setHoveredKey(null)
      setHoverPos(null)
    }
  }, [aircraft, hoveredKey])

  // Same, for a controller that logs off between polls.
  useEffect(() => {
    if (hoveredControllerKey && !atcControllers.some((c) => controllerKeyFor(c) === hoveredControllerKey)) {
      setHoveredControllerKey(null)
      setHoveredControllerPos(null)
    }
  }, [atcControllers, hoveredControllerKey])

  // Controllers currently working the hovered sector. Recomputed from the latest poll rather than
  // captured on hover, so a card left open while someone logs off updates instead of going stale.
  const hoveredSectorControllers = hoveredSectorId
    ? atcControllers.filter((c) => c.coverageKind === 'sector' && c.boundaryId === hoveredSectorId)
    : []

  // The last controller in a sector can log off while the pointer is still over its polygon.
  useEffect(() => {
    if (hoveredSectorId && hoveredSectorControllers.length === 0) {
      setHoveredSectorId(null)
      setHoveredSectorPos(null)
    }
  }, [hoveredSectorId, hoveredSectorControllers.length])

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

    map.on('error', (_event: MapLibreErrorEvent) => setTilesUnavailable(true))

    const updateHoverPosition = (key: string) => {
      const entry = markersRef.current.get(key)
      const current = mapRef.current
      const containerEl = containerRef.current
      if (!entry || !current || !containerEl) return
      const point = current.project(entry.marker.getLngLat())
      // Viewport-relative (not container-relative) so the card can render with `position: fixed`
      // and isn't clipped by the map container's `overflow-hidden` (needed for its rounded
      // corners over the raster basemap) when an aircraft is near an edge.
      const rect = containerEl.getBoundingClientRect()
      setHoverPos({ x: rect.left + point.x, y: rect.top + point.y })
    }

    const updateControllerHoverPosition = (key: string) => {
      const marker = controllerMarkersRef.current.get(key)
      const current = mapRef.current
      const containerEl = containerRef.current
      if (!marker || !current || !containerEl) return
      const point = current.project(marker.getLngLat())
      const rect = containerEl.getBoundingClientRect()
      setHoveredControllerPos({ x: rect.left + point.x, y: rect.top + point.y })
    }

    const publishViewport = () => {
      const current = mapRef.current
      const report = onViewportChangeRef.current
      if (!current || !report) return
      const bounds = current.getBounds()
      report({
        west: bounds.getWest(),
        south: bounds.getSouth(),
        east: bounds.getEast(),
        north: bounds.getNorth(),
      })
    }

    const sync = (fitBounds: boolean) => {
      const current = mapRef.current
      if (!current) return
      const {
        aircraft: currentAircraft,
        network: currentNetwork,
        atcControllers: currentAtc,
        atcBoundaries: currentBoundaries,
      } = latestRef.current

      const networkSource = current.getSource(NETWORK_SOURCE_ID) as GeoJSONSource | undefined
      if (networkSource) {
        networkSource.setData(buildNetworkFeatures(currentNetwork))
      }

      const atcSectorSource = current.getSource(ATC_SECTOR_SOURCE_ID) as GeoJSONSource | undefined
      if (atcSectorSource) {
        atcSectorSource.setData(buildAtcSectorFeatures(currentAtc, currentBoundaries))
      }

      const atcTerminalSource = current.getSource(ATC_TERMINAL_SOURCE_ID) as GeoJSONSource | undefined
      if (atcTerminalSource) {
        atcTerminalSource.setData(buildAtcTerminalFeatures(currentAtc))
      }

      // Controller markers - static positions (an airport doesn't move), so unlike aircraft this
      // is a plain add/remove/reposition diff with no animation.
      const positionedControllers = currentAtc.filter((c) => c.latitudeDeg != null && c.longitudeDeg != null)
      const nextControllerKeys = new Set(positionedControllers.map(controllerKeyFor))
      for (const [key, marker] of controllerMarkersRef.current) {
        if (!nextControllerKeys.has(key)) {
          marker.remove()
          controllerMarkersRef.current.delete(key)
        }
      }
      for (const controller of positionedControllers) {
        const key = controllerKeyFor(controller)
        const label = `${controller.callsign} - ${controller.facilityLabel}${controller.airportName ? ` (${controller.airportName})` : ''}, ${controller.frequency}`
        const existing = controllerMarkersRef.current.get(key)
        if (existing) {
          existing.setLngLat([controller.longitudeDeg!, controller.latitudeDeg!])
          existing.getElement().setAttribute('aria-label', label)
          if (hoveredControllerKeyRef.current === key) updateControllerHoverPosition(key)
          continue
        }

        const element = controllerMarkerElement(label)
        const marker = new Marker({ element, anchor: 'center' })
          .setLngLat([controller.longitudeDeg!, controller.latitudeDeg!])
          .addTo(current)
        element.setAttribute('aria-label', label)
        controllerMarkersRef.current.set(key, marker)

        element.addEventListener('mouseenter', () => {
          setHoveredControllerKey(key)
          updateControllerHoverPosition(key)
        })
        element.addEventListener('mouseleave', () => {
          setHoveredControllerKey((k) => (k === key ? null : k))
        })
      }

      // Airport dots - static, deduplicated by ICAO.
      const airports = networkAirports(currentNetwork)
      const nextIcaos = new Set(airports.map((a) => a.icao))
      for (const [icao, marker] of airportMarkersRef.current) {
        if (!nextIcaos.has(icao)) {
          marker.remove()
          airportMarkersRef.current.delete(icao)
        }
      }
      for (const airport of airports) {
        const existing = airportMarkersRef.current.get(airport.icao)
        if (existing) {
          existing.setLngLat([airport.lon, airport.lat])
        } else {
          const marker = new Marker({ element: airportDotElement(airport.icao), anchor: 'bottom' })
            .setLngLat([airport.lon, airport.lat])
            .addTo(current)
          airportMarkersRef.current.set(airport.icao, marker)
        }
      }

      // Aircraft markers - keyed so a refreshed poll updates/animates in place rather than
      // flickering a remove+recreate.
      const nextKeys = new Set(currentAircraft.map(keyFor))
      for (const [key, entry] of markersRef.current) {
        if (!nextKeys.has(key)) {
          if (entry.animationFrame !== null) cancelAnimationFrame(entry.animationFrame)
          entry.marker.remove()
          markersRef.current.delete(key)
        }
      }

      for (const ac of currentAircraft) {
        const key = keyFor(ac)
        const label =
          ac.kind === 'player'
            ? `Your flight${ac.flightNumber ? ` ${ac.flightNumber}` : ''} - click for details`
            : `Virtual flight${ac.flightNumber ? ` ${ac.flightNumber}` : ''} (${ac.pilotName})`
        const heading = ac.headingDeg ?? 0
        const existing = markersRef.current.get(key)

        if (existing && existing.kind === ac.kind) {
          existing.marker.getElement().setAttribute('aria-label', label)
          animateMarker(existing, ac.longitudeDeg, ac.latitudeDeg, heading)
          if (hoveredKeyRef.current === key) updateHoverPosition(key)
          continue
        }

        existing?.marker.remove()
        const element = aircraftMarkerElement(ac.kind, label)
        const marker = new Marker({ element, anchor: 'center', rotationAlignment: 'map' })
          .setLngLat([ac.longitudeDeg, ac.latitudeDeg])
          .setRotation(heading)
          .addTo(current)
        // Marker#addTo overwrites the element's aria-label with a generic "Map marker" - set the
        // real one after, not before, or it gets clobbered (same gotcha LiveFlightMap.tsx hit).
        element.setAttribute('aria-label', label)

        const entry: AircraftMarkerEntry = { marker, kind: ac.kind, animationFrame: null }
        markersRef.current.set(key, entry)

        element.addEventListener('mouseenter', () => {
          setHoveredKey(key)
          updateHoverPosition(key)
        })
        element.addEventListener('mouseleave', () => {
          setHoveredKey((k) => (k === key ? null : k))
        })
        if (ac.kind === 'player') {
          const activate = () => navigateRef.current('/fly')
          element.addEventListener('click', activate)
          element.addEventListener('keydown', (event: KeyboardEvent) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault()
              activate()
            }
          })
        }
      }

      if (fitBounds && !firedInitialFitRef.current) {
        const points: LonLat[] = [
          ...currentNetwork.flatMap((r): LonLat[] => [
            [r.departureLon, r.departureLat],
            [r.arrivalLon, r.arrivalLat],
          ]),
          ...currentAircraft.map((ac): LonLat => [ac.longitudeDeg, ac.latitudeDeg]),
        ]
        if (points.length > 0) {
          const bounds = boundsForPath(points)
          if (bounds) {
            firedInitialFitRef.current = true
            current.fitBounds(bounds, { padding: 56, maxZoom: 8, duration: 0 })
          }
        }
      }
    }

    map.on('style.load', () => {
      if (!map.getSource(NETWORK_SOURCE_ID)) {
        map.addSource(NETWORK_SOURCE_ID, { type: 'geojson', data: EMPTY_NETWORK_GEOJSON })
        map.addLayer({
          id: NETWORK_LAYER_ID,
          type: 'line',
          source: NETWORK_SOURCE_ID,
          layout: { 'line-cap': 'round', 'line-join': 'round' },
          paint: { 'line-color': readMapColors().accent, 'line-width': 1.5, 'line-opacity': 0.55 },
        })
      }
      if (!map.getSource(ATC_SECTOR_SOURCE_ID)) {
        const colors = readMapColors()
        map.addSource(ATC_SECTOR_SOURCE_ID, {
          type: 'geojson',
          data: EMPTY_ATC_SECTOR_GEOJSON,
          attribution: VATSPY_ATTRIBUTION,
        })
        map.addLayer({
          id: ATC_SECTOR_FILL_LAYER_ID,
          type: 'fill',
          source: ATC_SECTOR_SOURCE_ID,
          paint: { 'fill-color': colors.atcFill },
        })
        map.addLayer({
          id: ATC_SECTOR_LINE_LAYER_ID,
          type: 'line',
          source: ATC_SECTOR_SOURCE_ID,
          layout: { 'line-join': 'round' },
          paint: { 'line-color': colors.atc, 'line-width': 1.25, 'line-opacity': 0.75 },
        })

        // Terminal circles sit above the sectors so a tower inside a busy centre stays readable,
        // and are drawn dashed and fainter because they are an approximation, not a boundary.
        map.addSource(ATC_TERMINAL_SOURCE_ID, { type: 'geojson', data: EMPTY_ATC_TERMINAL_GEOJSON })
        map.addLayer({
          id: ATC_TERMINAL_FILL_LAYER_ID,
          type: 'fill',
          source: ATC_TERMINAL_SOURCE_ID,
          paint: { 'fill-color': colors.atcFillSoft },
        })
        map.addLayer({
          id: ATC_TERMINAL_LINE_LAYER_ID,
          type: 'line',
          source: ATC_TERMINAL_SOURCE_ID,
          paint: {
            'line-color': colors.atc,
            'line-width': 1,
            'line-opacity': 0.5,
            'line-dasharray': [2, 2],
          },
        })
      }
      styleReadyRef.current = true
      sync(true)
      publishViewport()
    })

    // A sector is an area, not a point, so it has no marker to hover - the polygon itself is the
    // target and the card follows the pointer.
    map.on('mousemove', ATC_SECTOR_FILL_LAYER_ID, (event) => {
      const feature = event.features?.[0]
      const boundaryId = feature?.properties?.boundaryId
      const containerEl = containerRef.current
      if (typeof boundaryId !== 'string' || !containerEl) return
      const rect = containerEl.getBoundingClientRect()
      setHoveredSectorId(boundaryId)
      setHoveredSectorPos({ x: rect.left + event.point.x, y: rect.top + event.point.y })
    })

    map.on('mouseleave', ATC_SECTOR_FILL_LAYER_ID, () => {
      setHoveredSectorId(null)
      setHoveredSectorPos(null)
    })

    map.on('move', () => {
      if (hoveredKeyRef.current) updateHoverPosition(hoveredKeyRef.current)
      if (hoveredControllerKeyRef.current) updateControllerHoverPosition(hoveredControllerKeyRef.current)
    })

    // Debounced so a flick's inertia settles into one update. This re-filters a list from data
    // already in hand - it never fetches, so panning cannot affect the feed's cadence however hard
    // the map is dragged.
    let viewportTimer: ReturnType<typeof setTimeout> | undefined
    const scheduleViewportPublish = () => {
      clearTimeout(viewportTimer)
      viewportTimer = setTimeout(publishViewport, VIEWPORT_DEBOUNCE_MS)
    }
    map.on('moveend', scheduleViewportPublish)
    map.on('zoomend', scheduleViewportPublish)

    const observer = new MutationObserver(() => {
      const current = mapRef.current
      if (!current) return
      const nextMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
      if (nextMode !== modeRef.current) {
        modeRef.current = nextMode
        current.once('style.load', () => sync(false))
        current.setStyle(buildMapStyle(nextMode, readMapColors()))
      }
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })

    const resync = () => sync(false)
    ;(map as unknown as { __fsopsResync?: () => void }).__fsopsResync = resync

    return () => {
      observer.disconnect()
      clearTimeout(viewportTimer)
      for (const entry of markersRef.current.values()) {
        if (entry.animationFrame !== null) cancelAnimationFrame(entry.animationFrame)
        entry.marker.remove()
      }
      markersRef.current.clear()
      for (const marker of airportMarkersRef.current.values()) marker.remove()
      airportMarkersRef.current.clear()
      for (const marker of controllerMarkersRef.current.values()) marker.remove()
      controllerMarkersRef.current.clear()
      map.remove()
      mapRef.current = null
      firedInitialFitRef.current = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mount-only; updates flow through latestRef + resync
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    const resync = (map as unknown as { __fsopsResync?: () => void }).__fsopsResync
    resync?.()
  }, [aircraft, network, atcControllers, atcBoundaries])

  const hasNetwork = network.length > 0
  const hasAircraft = aircraft.length > 0

  if (!hasNetwork) {
    return (
      <div className={cn('flex flex-col items-center justify-center gap-3 rounded-lg border border-border bg-surface py-16 text-center', className)}>
        <div className="flex size-12 items-center justify-center rounded-full bg-muted text-muted-foreground">
          <RouteIcon className="size-5" />
        </div>
        <div className="space-y-1 px-6">
          <p className="text-sm font-medium">No network to show yet</p>
          <p className="mx-auto max-w-sm text-sm text-muted-foreground">
            Create a route and it will appear here as part of your network.
          </p>
        </div>
        <Button asChild size="sm" variant="outline">
          <Link to="/routes">Go to Routes</Link>
        </Button>
      </div>
    )
  }

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[320px]" role="application" aria-label="Live operations map" />

      {(tilesUnavailable || atcUnavailable) && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 flex flex-col items-start gap-1.5">
          {tilesUnavailable && (
            <div className="rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
              Map tiles unavailable — showing offline view
            </div>
          )}
          {atcUnavailable && (
            <div className="rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
              ATC data unavailable
            </div>
          )}
        </div>
      )}

      {!hasAircraft && (
        <div className="pointer-events-none absolute inset-x-3 bottom-3 z-10 flex justify-center">
          <div className="pointer-events-auto flex max-w-md flex-col items-center gap-2 rounded-lg border border-border bg-surface-elevated/95 p-4 text-center shadow-elevation-3">
            <div className="flex size-9 shrink-0 items-center justify-center rounded-full bg-accent/15 text-accent">
              <PlaneTakeoff className="size-4" />
            </div>
            <p className="text-sm font-medium">Nothing airborne right now</p>
            <p className="text-xs text-muted-foreground">
              Hire a pilot and give them a schedule, or fly a route yourself, and it will show up here live.
            </p>
            <div className="flex gap-2 pt-1">
              <Button asChild size="sm" variant="outline">
                <Link to="/pilots">
                  <Users className="size-3.5 shrink-0" />
                  Pilots
                </Link>
              </Button>
              <Button asChild size="sm">
                <Link to="/fly">
                  <PlaneTakeoff className="size-3.5 shrink-0" />
                  Fly
                </Link>
              </Button>
            </div>
          </div>
        </div>
      )}

      {(hasAircraft || atcControllers.length > 0) && (
        <div className="pointer-events-none absolute bottom-3 right-3 z-10 flex flex-col gap-1 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1.5 text-xs text-muted-foreground shadow-elevation-2">
          {hasAircraft && (
            <div className="flex items-center gap-3">
              <span className="flex items-center gap-1.5">
                <span className="size-2.5 shrink-0 rounded-full bg-accent" />
                You
              </span>
              <span className="flex items-center gap-1.5">
                <span className="size-2.5 shrink-0 rounded-full border-2 border-accent/70 bg-surface-elevated" />
                Virtual
              </span>
            </div>
          )}
          {atcControllers.length > 0 && (
            <>
              {hasAircraft && <span className="h-px bg-border" aria-hidden="true" />}
              <span className="flex items-center gap-1.5">
                <span className="size-2.5 shrink-0 rounded-full border-2 border-warning bg-surface-elevated" />
                ATC
              </span>
              <AtcMapLegend controllers={atcControllers} />
            </>
          )}
        </div>
      )}

      {hoveredAircraft && hoverPos && (
        <LiveFlightCard aircraft={hoveredAircraft} x={hoverPos.x} y={hoverPos.y} />
      )}

      {hoveredController && hoveredControllerPos && (
        <AtcControllerCard controller={hoveredController} x={hoveredControllerPos.x} y={hoveredControllerPos.y} />
      )}

      {hoveredSectorId && hoveredSectorPos && hoveredSectorControllers.length > 0 && (
        <AtcSectorCard
          boundaryName={hoveredSectorControllers[0]!.boundaryName ?? hoveredSectorId}
          controllers={hoveredSectorControllers}
          x={hoveredSectorPos.x}
          y={hoveredSectorPos.y}
        />
      )}
    </div>
  )
}
