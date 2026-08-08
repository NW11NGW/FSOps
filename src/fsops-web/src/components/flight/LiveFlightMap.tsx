import { useEffect, useRef, useState } from 'react'
import type { Feature, FeatureCollection, LineString } from 'geojson'
import { Map as MapLibreMap, Marker } from 'maplibre-gl'
import type { GeoJSONSource } from 'maplibre-gl'

type MapLibreErrorEvent = { error?: { message?: string } }
import 'maplibre-gl/dist/maplibre-gl.css'

import { buildMapStyle, readMapColors, type MapThemeMode } from '@/components/map/mapTheme'
import { boundsForPath, splitAntimeridian } from '@/lib/geo'
import { cn } from '@/lib/utils'
import type { LonLat } from '@/types/route'

const ROUTE_SOURCE_ID = 'live-route'
const ROUTE_LAYER_ID = 'live-route-line'
const EMPTY_GEOJSON: FeatureCollection<LineString> = { type: 'FeatureCollection', features: [] }

interface LiveAirportPoint {
  icao: string
  latitude: number
  longitude: number
}

interface LiveAircraftPoint {
  latitude: number
  longitude: number
  headingDeg: number
}

interface LiveFlightMapProps {
  departure: LiveAirportPoint | null
  arrival: LiveAirportPoint | null
  path: LonLat[]
  aircraft: LiveAircraftPoint | null
  className?: string
}

function airportMarkerElement(label: string, filled: boolean): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex flex-col items-center gap-1'
  const dot = document.createElement('div')
  dot.className = cn(
    'size-3 rounded-full border-2 shadow-elevation-2',
    filled ? 'border-background bg-accent' : 'border-accent bg-background',
  )
  wrapper.appendChild(dot)
  const tag = document.createElement('div')
  tag.className = 'rounded border border-border bg-surface-elevated px-1.5 py-0.5 font-mono text-[10px] font-semibold shadow-elevation-2'
  tag.textContent = label
  wrapper.appendChild(tag)
  return wrapper
}

/** Plane glyph as a raw SVG string (no external icon dependency) - rotated to true heading via CSS transform on the wrapper. */
const AIRCRAFT_SVG =
  '<svg viewBox="0 0 24 24" fill="currentColor" width="22" height="22"><path d="M22 16.5v-2l-8.5-5V4.5c0-1-.8-2-1.5-2s-1.5 1-1.5 2v5l-8.5 5v2l8.5-2.5V19l-2.5 1.8V22l4-1 4 1v-1.2L13.5 19v-5.5z"/></svg>'

function aircraftMarkerElement(): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex items-center justify-center rounded-full bg-accent text-accent-foreground shadow-elevation-3 ring-2 ring-background'
  wrapper.style.width = '30px'
  wrapper.style.height = '30px'
  wrapper.style.willChange = 'transform'
  wrapper.innerHTML = AIRCRAFT_SVG
  wrapper.setAttribute('role', 'img')
  wrapper.setAttribute('aria-label', 'Aircraft position')
  return wrapper
}

/**
 * A purpose-built live map for the in-progress flight view: static route line plus departure/
 * arrival markers (visually matching RouteMap's theme) and one continuously-updated aircraft
 * marker rotated to its true heading. RouteMap has no live-marker API, so this is a lightweight
 * sibling rather than an extension of it, sharing the same basemap and theme helpers for visual
 * consistency.
 */
export function LiveFlightMap({ departure, arrival, path, aircraft, className }: LiveFlightMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const aircraftMarkerRef = useRef<Marker | null>(null)
  const aircraftIconRef = useRef<HTMLDivElement | null>(null)
  const depMarkerRef = useRef<Marker | null>(null)
  const arrMarkerRef = useRef<Marker | null>(null)
  const modeRef = useRef<MapThemeMode>('dark')
  const styleReadyRef = useRef(false)
  const firedInitialFitRef = useRef(false)
  const latestRef = useRef({ departure, arrival, path, aircraft })
  const [tilesUnavailable, setTilesUnavailable] = useState(false)

  latestRef.current = { departure, arrival, path, aircraft }

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

    map.on('error', (_event: MapLibreErrorEvent) => setTilesUnavailable(true))

    const sync = (fitBounds: boolean) => {
      const current = mapRef.current
      if (!current) return
      const { departure: dep, arrival: arr, path: currentPath, aircraft: ac } = latestRef.current

      const source = current.getSource(ROUTE_SOURCE_ID) as GeoJSONSource | undefined
      if (source) {
        const features: Feature<LineString>[] = splitAntimeridian(currentPath).map((coordinates) => ({
          type: 'Feature',
          properties: {},
          geometry: { type: 'LineString', coordinates },
        }))
        source.setData({ type: 'FeatureCollection', features })
      }

      if (dep) {
        if (!depMarkerRef.current) {
          depMarkerRef.current = new Marker({ element: airportMarkerElement(dep.icao, true), anchor: 'bottom' })
            .setLngLat([dep.longitude, dep.latitude])
            .addTo(current)
        } else {
          depMarkerRef.current.setLngLat([dep.longitude, dep.latitude])
        }
      }
      if (arr) {
        if (!arrMarkerRef.current) {
          arrMarkerRef.current = new Marker({ element: airportMarkerElement(arr.icao, false), anchor: 'bottom' })
            .setLngLat([arr.longitude, arr.latitude])
            .addTo(current)
        } else {
          arrMarkerRef.current.setLngLat([arr.longitude, arr.latitude])
        }
      }

      if (ac) {
        if (!aircraftMarkerRef.current) {
          const el = aircraftMarkerElement()
          aircraftIconRef.current = el
          aircraftMarkerRef.current = new Marker({ element: el, anchor: 'center' }).setLngLat([ac.longitude, ac.latitude]).addTo(current)
        } else {
          aircraftMarkerRef.current.setLngLat([ac.longitude, ac.latitude])
        }
        if (aircraftIconRef.current) {
          aircraftIconRef.current.style.transform = `rotate(${ac.headingDeg}deg)`
        }
      }

      if (fitBounds && !firedInitialFitRef.current) {
        const points: LonLat[] = currentPath.length > 1 ? currentPath : []
        if (dep) points.push([dep.longitude, dep.latitude])
        if (arr) points.push([arr.longitude, arr.latitude])
        if (points.length > 0) {
          const bounds = boundsForPath(points)
          if (bounds) {
            firedInitialFitRef.current = true
            current.fitBounds(bounds, { padding: 56, maxZoom: 9, duration: 0 })
          }
        } else if (ac) {
          current.jumpTo({ center: [ac.longitude, ac.latitude], zoom: 6 })
        }
      }
    }

    map.on('style.load', () => {
      if (!map.getSource(ROUTE_SOURCE_ID)) {
        map.addSource(ROUTE_SOURCE_ID, { type: 'geojson', data: EMPTY_GEOJSON })
        map.addLayer({
          id: ROUTE_LAYER_ID,
          type: 'line',
          source: ROUTE_SOURCE_ID,
          layout: { 'line-cap': 'round', 'line-join': 'round' },
          paint: { 'line-color': readMapColors().accent, 'line-width': 2.5, 'line-opacity': 0.8 },
        })
      }
      styleReadyRef.current = true
      sync(true)
    })

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
      depMarkerRef.current?.remove()
      arrMarkerRef.current?.remove()
      aircraftMarkerRef.current?.remove()
      depMarkerRef.current = null
      arrMarkerRef.current = null
      aircraftMarkerRef.current = null
      map.remove()
      mapRef.current = null
      firedInitialFitRef.current = false
    }
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    const resync = (map as unknown as { __fsopsResync?: () => void }).__fsopsResync
    resync?.()
  }, [departure, arrival, path, aircraft])

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[240px]" role="application" aria-label="Live flight position map" />
      {tilesUnavailable && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
          Map tiles unavailable — showing offline view
        </div>
      )}
    </div>
  )
}
