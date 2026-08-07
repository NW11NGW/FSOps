import { useEffect, useRef, useState } from 'react'
import type { FeatureCollection, LineString } from 'geojson'
import { MapLibreMap, Marker, NavigationControl } from 'maplibre-gl'
import type { ErrorEvent as MapLibreErrorEvent, GeoJSONSource } from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'

import { boundsForPath, splitAntimeridian } from '@/lib/geo'
import { cn } from '@/lib/utils'
import type { AirportSummary } from '@/types/airport'
import type { LonLat } from '@/types/route'

import { buildMapStyle, readMapColors, type MapColors, type MapThemeMode } from './mapTheme'

const ROUTE_SOURCE_ID = 'route'
const ROUTE_GLOW_LAYER_ID = 'route-glow'
const ROUTE_LINE_LAYER_ID = 'route-line'

const EMPTY_ROUTE_GEOJSON: FeatureCollection<LineString> = { type: 'FeatureCollection', features: [] }

interface RouteMapProps {
  departure: AirportSummary | null
  arrival: AirportSummary | null
  path: LonLat[]
  className?: string
}

interface MarkerRefs {
  departure: Marker | null
  arrival: Marker | null
}

function routeGeoJson(path: LonLat[]): FeatureCollection<LineString> {
  const segments = splitAntimeridian(path)
  if (segments.length === 0) return EMPTY_ROUTE_GEOJSON
  return {
    type: 'FeatureCollection',
    features: segments.map((coordinates) => ({
      type: 'Feature',
      properties: {},
      geometry: { type: 'LineString', coordinates },
    })),
  }
}

function createMarkerElement(role: 'departure' | 'arrival', icao: string): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex flex-col items-center gap-1'
  wrapper.dataset.icao = icao

  const dot = document.createElement('div')
  dot.className =
    role === 'departure'
      ? 'size-3.5 rounded-full border-2 border-background bg-accent shadow-elevation-2'
      : 'size-3.5 rounded-full border-2 border-accent bg-background shadow-elevation-2'
  wrapper.appendChild(dot)

  const label = document.createElement('div')
  label.className =
    'rounded border border-border bg-surface-elevated px-1.5 py-0.5 font-mono text-[10px] font-semibold tracking-wide text-foreground shadow-elevation-2'
  label.textContent = icao
  wrapper.appendChild(label)

  return wrapper
}

function ensureRouteLayers(map: MapLibreMap, colors: MapColors): void {
  if (map.getSource(ROUTE_SOURCE_ID)) return

  map.addSource(ROUTE_SOURCE_ID, { type: 'geojson', data: EMPTY_ROUTE_GEOJSON })
  map.addLayer({
    id: ROUTE_GLOW_LAYER_ID,
    type: 'line',
    source: ROUTE_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': colors.accentGlow, 'line-width': 7, 'line-blur': 4 },
  })
  map.addLayer({
    id: ROUTE_LINE_LAYER_ID,
    type: 'line',
    source: ROUTE_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': colors.accent, 'line-width': 2.5 },
  })
}

function applyRouteColors(map: MapLibreMap, colors: MapColors): void {
  if (!map.getLayer(ROUTE_GLOW_LAYER_ID) || !map.getLayer(ROUTE_LINE_LAYER_ID)) return
  map.setPaintProperty(ROUTE_GLOW_LAYER_ID, 'line-color', colors.accentGlow)
  map.setPaintProperty(ROUTE_LINE_LAYER_ID, 'line-color', colors.accent)
}

function updateMarker(
  map: MapLibreMap,
  markers: MarkerRefs,
  role: 'departure' | 'arrival',
  airport: AirportSummary | null,
): void {
  const existing = markers[role]

  if (!airport) {
    existing?.remove()
    markers[role] = null
    return
  }

  const lngLat: [number, number] = [airport.longitude, airport.latitude]

  if (existing && existing.getElement().dataset.icao === airport.icao) {
    existing.setLngLat(lngLat)
    return
  }

  existing?.remove()
  markers[role] = new Marker({ element: createMarkerElement(role, airport.icao), anchor: 'bottom' })
    .setLngLat(lngLat)
    .addTo(map)
}

function syncMarkers(map: MapLibreMap, markers: MarkerRefs, departure: AirportSummary | null, arrival: AirportSummary | null): void {
  updateMarker(map, markers, 'departure', departure)
  updateMarker(map, markers, 'arrival', arrival)
}

function fitToRoute(
  map: MapLibreMap,
  departure: AirportSummary | null,
  arrival: AirportSummary | null,
  path: LonLat[],
  animate: boolean,
): void {
  const points: LonLat[] = path.length > 1 ? path : []
  if (points.length === 0) {
    if (departure) points.push([departure.longitude, departure.latitude])
    if (arrival) points.push([arrival.longitude, arrival.latitude])
  }
  if (points.length === 0) return

  if (points.length === 1) {
    map.easeTo({ center: points[0], zoom: Math.max(map.getZoom(), 5), duration: animate ? 800 : 0, essential: true })
    return
  }

  const bounds = boundsForPath(points)
  if (!bounds) return

  map.fitBounds(bounds, {
    padding: { top: 56, bottom: 56, left: 56, right: 56 },
    duration: animate ? 800 : 0,
    maxZoom: 9,
    essential: true,
  })
}

/**
 * Dark/light MapLibre basemap (CARTO raster tiles) with the previewed route's great-circle arc
 * and departure/arrival markers. Tiles require internet; if they fail to load the themed
 * background layer plus route/markers still render normally (see mapTheme.ts).
 */
export function RouteMap({ departure, arrival, path, className }: RouteMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const markersRef = useRef<MarkerRefs>({ departure: null, arrival: null })
  const modeRef = useRef<MapThemeMode>('dark')
  const latestRef = useRef({ departure, arrival, path })
  // Tracks readiness independently of MapLibre's own isStyleLoaded()/loaded(), which for a
  // raster source stays false until its tiles have actually downloaded - offline, that never
  // happens, so gating on it would mean the route line and markers never appear without
  // internet. 'style.load' fires as soon as the style JSON + source definitions are parsed,
  // before any tile request is made, which is the point sources/layers become safe to add.
  const styleReadyRef = useRef(false)
  const [tilesUnavailable, setTilesUnavailable] = useState(false)

  latestRef.current = { departure, arrival, path }

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

    const sync = (animate: boolean) => {
      const current = mapRef.current
      if (!current) return
      ensureRouteLayers(current, readMapColors())
      const { departure: dep, arrival: arr, path: currentPath } = latestRef.current
      const source = current.getSource(ROUTE_SOURCE_ID) as GeoJSONSource | undefined
      source?.setData(routeGeoJson(currentPath))
      syncMarkers(current, markersRef.current, dep, arr)
      fitToRoute(current, dep, arr, currentPath, animate)
    }

    map.on('style.load', () => {
      styleReadyRef.current = true
      sync(false)
    })

    const observer = new MutationObserver(() => {
      const current = mapRef.current
      if (!current) return

      const nextMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
      if (nextMode !== modeRef.current) {
        modeRef.current = nextMode
        styleReadyRef.current = false
        current.once('style.load', () => {
          styleReadyRef.current = true
          sync(false)
        })
        current.setStyle(buildMapStyle(nextMode, readMapColors()))
      } else {
        applyRouteColors(current, readMapColors())
      }
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] })

    return () => {
      observer.disconnect()
      markersRef.current.departure?.remove()
      markersRef.current.arrival?.remove()
      markersRef.current = { departure: null, arrival: null }
      map.remove()
      mapRef.current = null
    }
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    ensureRouteLayers(map, readMapColors())
    const source = map.getSource(ROUTE_SOURCE_ID) as GeoJSONSource | undefined
    if (!source) return
    source.setData(routeGeoJson(path))
    syncMarkers(map, markersRef.current, departure, arrival)
    fitToRoute(map, departure, arrival, path, true)
  }, [departure, arrival, path])

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[320px]" role="application" aria-label="Route map" />
      {tilesUnavailable && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
          Map tiles unavailable — showing offline view
        </div>
      )}
    </div>
  )
}
