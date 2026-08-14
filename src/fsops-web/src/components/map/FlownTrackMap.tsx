import { useEffect, useRef, useState } from 'react'
import type { Feature, FeatureCollection, LineString } from 'geojson'
import { Map as MapLibreMap, Marker, NavigationControl } from 'maplibre-gl'
import type { GeoJSONSource } from 'maplibre-gl'

type MapLibreErrorEvent = { error?: { message?: string } }
import 'maplibre-gl/dist/maplibre-gl.css'

import { buildMapStyle, readMapColors, readTokenRgb, type MapThemeMode } from './mapTheme'
import { boundsForPath, splitAntimeridian } from '@/lib/geo'
import { cn } from '@/lib/utils'
import type { LonLat } from '@/types/route'

const PLANNED_SOURCE_ID = 'track-planned'
const PLANNED_LAYER_ID = 'track-planned-line'
const FLOWN_SOURCE_ID = 'track-flown'
const FLOWN_LAYER_ID = 'track-flown-line'

const EMPTY_GEOJSON: FeatureCollection<LineString> = { type: 'FeatureCollection', features: [] }

interface FlownTrackMapProps {
  /** The actual recorded path, oldest point first. */
  flownPath: LonLat[]
  /** The great-circle the sector was planned along, for comparison. Empty when the airports could
   *  not be resolved - the flown track still draws on its own. */
  plannedPath: LonLat[]
  departureIcao: string | null
  arrivalIcao: string | null
  className?: string
}

function toFeatures(path: LonLat[]): FeatureCollection<LineString> {
  // splitAntimeridian is what stops a Pacific crossing being drawn as one straight line all the way
  // back across the map. A track with fewer than two points produces no segments at all, which is
  // correct: one position is not a path, and joining it to nothing would be an invented line.
  const features: Feature<LineString>[] = splitAntimeridian(path).map((coordinates) => ({
    type: 'Feature',
    properties: {},
    geometry: { type: 'LineString', coordinates },
  }))
  return { type: 'FeatureCollection', features }
}

function endpointMarkerElement(label: string, tone: 'start' | 'end'): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex flex-col items-center gap-1'
  const dot = document.createElement('div')
  dot.className = cn(
    'size-3 rounded-full border-2 shadow-elevation-2',
    tone === 'start' ? 'border-background bg-accent' : 'border-accent bg-background',
  )
  wrapper.appendChild(dot)
  const tag = document.createElement('div')
  tag.className =
    'rounded border border-border bg-surface-elevated px-1.5 py-0.5 font-mono text-[10px] font-semibold text-foreground shadow-elevation-2'
  tag.textContent = label
  wrapper.appendChild(tag)
  return wrapper
}

/**
 * The path a flight actually flew, drawn over the great circle it was planned along.
 *
 * Two lines, and the difference between them is the whole point: the planned line is the dashed,
 * muted one; the flown line is solid and in the accent colour. Vectors, holds, a diversion and a
 * long downwind all show up as the flown line departing from the planned one.
 *
 * Renders nothing but its own container when `flownPath` has fewer than two points - a single
 * recorded position still gets a marker so the player can see where it was, but no line is drawn
 * through it. Callers are expected to show an explanatory empty state instead of mounting this at
 * all when there is no track; see FlightTrackCard.
 */
export function FlownTrackMap({ flownPath, plannedPath, departureIcao, arrivalIcao, className }: FlownTrackMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const markersRef = useRef<Marker[]>([])
  const modeRef = useRef<MapThemeMode>('dark')
  const styleReadyRef = useRef(false)
  const hasFittedRef = useRef(false)
  const latestRef = useRef({ flownPath, plannedPath, departureIcao, arrivalIcao })
  const [tilesUnavailable, setTilesUnavailable] = useState(false)

  latestRef.current = { flownPath, plannedPath, departureIcao, arrivalIcao }

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

    const ensureLayers = () => {
      const colors = readMapColors()
      if (!map.getSource(PLANNED_SOURCE_ID)) {
        map.addSource(PLANNED_SOURCE_ID, { type: 'geojson', data: EMPTY_GEOJSON })
        map.addLayer({
          id: PLANNED_LAYER_ID,
          type: 'line',
          source: PLANNED_SOURCE_ID,
          layout: { 'line-cap': 'round', 'line-join': 'round' },
          paint: {
            'line-color': colors.mutedForeground,
            'line-width': 1.5,
            'line-opacity': 0.7,
            // Dashed and thin so the plan reads as a reference, never as a second flown track.
            'line-dasharray': [2, 2],
          },
        })
      }
      if (!map.getSource(FLOWN_SOURCE_ID)) {
        map.addSource(FLOWN_SOURCE_ID, { type: 'geojson', data: EMPTY_GEOJSON })
        map.addLayer({
          id: FLOWN_LAYER_ID,
          type: 'line',
          source: FLOWN_SOURCE_ID,
          layout: { 'line-cap': 'round', 'line-join': 'round' },
          paint: { 'line-color': readTokenRgb('--accent'), 'line-width': 2.5, 'line-opacity': 0.95 },
        })
      }
    }

    const sync = () => {
      const current = mapRef.current
      if (!current) return
      const { flownPath: flown, plannedPath: planned, departureIcao: dep, arrivalIcao: arr } = latestRef.current

      ensureLayers()
      ;(current.getSource(PLANNED_SOURCE_ID) as GeoJSONSource | undefined)?.setData(toFeatures(planned))
      ;(current.getSource(FLOWN_SOURCE_ID) as GeoJSONSource | undefined)?.setData(toFeatures(flown))

      for (const marker of markersRef.current) marker.remove()
      markersRef.current = []

      // Markers sit on where the aircraft actually STARTED and ENDED, not on the airports - for a
      // diversion those are different places, and the map should show what happened rather than
      // what was intended. Falls back to the planned endpoints when there is no flown track.
      const first = flown[0] ?? planned[0]
      const last = flown[flown.length - 1] ?? planned[planned.length - 1]
      if (first) {
        const marker = new Marker({ element: endpointMarkerElement(dep ?? 'Start', 'start'), anchor: 'bottom' })
          .setLngLat(first)
          .addTo(current)
        marker.getElement().setAttribute('aria-label', dep ? `Departure ${dep}` : 'Start of flown track')
        markersRef.current.push(marker)
      }
      if (last && last !== first) {
        const marker = new Marker({ element: endpointMarkerElement(arr ?? 'End', 'end'), anchor: 'bottom' })
          .setLngLat(last)
          .addTo(current)
        marker.getElement().setAttribute('aria-label', arr ? `Arrival ${arr}` : 'End of flown track')
        markersRef.current.push(marker)
      }

      if (!hasFittedRef.current) {
        // Fit to the FLOWN track when there is one - a track that wandered well off the planned
        // line must be fully visible, and fitting to the plan would crop exactly the part worth
        // looking at.
        const fitPoints = flown.length > 1 ? flown : planned.length > 1 ? planned : first ? [first] : []
        if (fitPoints.length === 1) {
          hasFittedRef.current = true
          current.jumpTo({ center: fitPoints[0], zoom: 8 })
        } else if (fitPoints.length > 1) {
          const bounds = boundsForPath(fitPoints)
          if (bounds) {
            hasFittedRef.current = true
            current.fitBounds(bounds, { padding: 48, maxZoom: 9, duration: 0 })
          }
        }
      }
    }

    map.on('style.load', () => {
      styleReadyRef.current = true
      sync()
    })

    const observer = new MutationObserver(() => {
      const current = mapRef.current
      if (!current) return
      const nextMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
      if (nextMode === modeRef.current) return
      modeRef.current = nextMode
      styleReadyRef.current = false
      current.once('style.load', () => {
        styleReadyRef.current = true
        sync()
      })
      current.setStyle(buildMapStyle(nextMode, readMapColors()))
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] })

    ;(map as unknown as { __fsopsResync?: () => void }).__fsopsResync = sync

    return () => {
      observer.disconnect()
      for (const marker of markersRef.current) marker.remove()
      markersRef.current = []
      map.remove()
      mapRef.current = null
      hasFittedRef.current = false
    }
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    ;(map as unknown as { __fsopsResync?: () => void }).__fsopsResync?.()
  }, [flownPath, plannedPath, departureIcao, arrivalIcao])

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[240px]" role="application" aria-label="Flown track map" />
      {tilesUnavailable && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
          Map tiles unavailable — showing offline view
        </div>
      )}
    </div>
  )
}

export default FlownTrackMap
