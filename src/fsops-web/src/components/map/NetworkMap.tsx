import { useEffect, useRef, useState } from 'react'
import type { Feature, FeatureCollection, LineString } from 'geojson'
import { Map as MapLibreMap, Marker, NavigationControl } from 'maplibre-gl'
import type { GeoJSONSource, MapLayerMouseEvent } from 'maplibre-gl'

type MapLibreErrorEvent = { error?: { message?: string } }
import 'maplibre-gl/dist/maplibre-gl.css'

import { buildMapStyle, readMapColors, readTokenRgb, type MapThemeMode } from './mapTheme'
import { boundsForPath, splitAntimeridian } from '@/lib/geo'
import { BAND_TOKEN, type NetworkBand } from '@/lib/networkHealth'
import { cn } from '@/lib/utils'
import type { LonLat } from '@/types/route'

const LINK_SOURCE_ID = 'network-links'
const LINK_GLOW_LAYER_ID = 'network-link-glow'
const LINK_LINE_LAYER_ID = 'network-link-line'
const LINK_HIT_LAYER_ID = 'network-link-hit'

const LINK_LAYER_IDS = [LINK_GLOW_LAYER_ID, LINK_LINE_LAYER_ID, LINK_HIT_LAYER_ID] as const

const EMPTY_GEOJSON: FeatureCollection<LineString, LinkFeatureProps> = { type: 'FeatureCollection', features: [] }

interface LinkFeatureProps {
  pairKey: string
  /** Resolved rgb() string for this link's band, baked into the feature so one line layer can paint
   *  the whole network in four different colours without four layers or a per-render restyle. */
  color: string
}

/** One arc to draw: a city pair, its great-circle path, and which band it falls into. */
export interface NetworkArc {
  pairKey: string
  path: LonLat[]
  band: NetworkBand
}

export interface NetworkMapAirport {
  icao: string
  latitude: number
  longitude: number
}

interface NetworkMapProps {
  arcs: NetworkArc[]
  airports: NetworkMapAirport[]
  homeAirportIcao: string | null
  selectedPairKey: string | null
  onSelectPair: (pairKey: string | null) => void
  className?: string
}

function buildFeatures(arcs: NetworkArc[]): FeatureCollection<LineString, LinkFeatureProps> {
  const features: Feature<LineString, LinkFeatureProps>[] = []
  for (const arc of arcs) {
    const color = readTokenRgb(BAND_TOKEN[arc.band])
    // splitAntimeridian turns one Pacific-crossing arc into two segments so it draws as two short
    // arcs at the map edges rather than a straight line the wrong way round the planet. Both
    // segments carry the same pairKey, so hovering either half selects the same route.
    for (const coordinates of splitAntimeridian(arc.path)) {
      features.push({
        type: 'Feature',
        properties: { pairKey: arc.pairKey, color },
        geometry: { type: 'LineString', coordinates },
      })
    }
  }
  return { type: 'FeatureCollection', features }
}

function ensureLayers(map: MapLibreMap, foreground: string): void {
  const allPresent = Boolean(map.getSource(LINK_SOURCE_ID)) && LINK_LAYER_IDS.every((id) => map.getLayer(id))
  if (allPresent) return

  // Same defensive teardown-and-rebuild as RouteMap: a style reload can leave the source and its
  // layers out of sync, and every probe is wrapped because during a style transition MapLibre can
  // report a source as absent while still holding it internally - trusting those probes is exactly
  // what previously left a fully-populated source with no layer to paint it.
  for (const id of LINK_LAYER_IDS) {
    try {
      if (map.getLayer(id)) map.removeLayer(id)
    } catch {
      // Already gone.
    }
  }
  try {
    if (map.getSource(LINK_SOURCE_ID)) map.removeSource(LINK_SOURCE_ID)
  } catch {
    // Already gone, or still referenced; addSource below tolerates both.
  }

  try {
    map.addSource(LINK_SOURCE_ID, { type: 'geojson', data: EMPTY_GEOJSON, promoteId: 'pairKey' })
  } catch {
    // The source survived the teardown - reuse it rather than losing the layers below.
  }

  map.addLayer({
    id: LINK_GLOW_LAYER_ID,
    type: 'line',
    source: LINK_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      'line-color': ['get', 'color'],
      'line-width': 9,
      'line-blur': 4,
      // Only the selected/hovered link glows, so a dense network never turns into one bloom.
      'line-opacity': [
        'case',
        ['==', ['feature-state', 'emphasis'], 'selected'],
        0.5,
        ['==', ['feature-state', 'emphasis'], 'hovered'],
        0.3,
        0,
      ],
    },
  })

  map.addLayer({
    id: LINK_LINE_LAYER_ID,
    type: 'line',
    source: LINK_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      // Colour is data, not state: it says how the route is doing, so it must never change on
      // hover. Width and opacity carry emphasis instead. Both are static-per-feature reads rather
      // than feature-state expressions on THIS layer - driving width from feature-state here is
      // what previously left the whole network unpainted in RouteMap - so emphasis lives entirely
      // in the glow layer above.
      'line-color': ['get', 'color'],
      'line-width': 2.5,
      'line-opacity': 0.95,
    },
  })

  // Wide, invisible hit target so a thin arc is still easy to hover and click.
  map.addLayer({
    id: LINK_HIT_LAYER_ID,
    type: 'line',
    source: LINK_SOURCE_ID,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': foreground, 'line-width': 18, 'line-opacity': 0 },
  })
}

function airportMarkerElement(icao: string, isHub: boolean): HTMLDivElement {
  const wrapper = document.createElement('div')
  wrapper.className = 'flex flex-col items-center gap-1'
  wrapper.title = isHub ? `${icao} — home base` : icao

  const dot = document.createElement('div')
  dot.className = cn(
    'rounded-full border-2 shadow-elevation-2',
    isHub ? 'size-4 border-background bg-accent ring-4 ring-accent/30' : 'size-2.5 border-accent/70 bg-surface-elevated',
  )
  wrapper.appendChild(dot)

  const label = document.createElement('div')
  label.className =
    'rounded border border-accent/40 bg-surface-elevated px-1.5 py-0.5 font-mono text-[10px] font-semibold tracking-wide text-foreground shadow-elevation-2'
  label.textContent = icao
  wrapper.appendChild(label)

  return wrapper
}

/**
 * The airline's whole network on one map, every city pair coloured by how it is actually doing.
 *
 * Colour is the point of this component and is deliberately the only thing that carries meaning:
 * it comes from the band in `arcs` (see lib/networkHealth for how a band is decided and the single
 * sentence that justifies each one) and never changes on hover or selection. Emphasis is carried by
 * a separate glow layer, so the answer to "is this route any good" is readable at a glance without
 * touching anything.
 *
 * Structurally a sibling of RouteMap rather than an extension of it: RouteMap is a route-PLANNING
 * surface, with draggable endpoints, a live preview arc and click-to-assign airports, none of which
 * belongs on a read-only performance view. They share the basemap, theme handling and antimeridian
 * splitting, which is where the real complexity lives.
 */
export function NetworkMap({ arcs, airports, homeAirportIcao, selectedPairKey, onSelectPair, className }: NetworkMapProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)
  const markersRef = useRef<Map<string, Marker>>(new Map())
  const modeRef = useRef<MapThemeMode>('dark')
  const styleReadyRef = useRef(false)
  const hasLoadedOnceRef = useRef(false)
  const hasFittedRef = useRef(false)
  const hoveredRef = useRef<string | null>(null)
  const latestRef = useRef({ arcs, airports, homeAirportIcao, selectedPairKey })
  const onSelectRef = useRef(onSelectPair)
  const [tilesUnavailable, setTilesUnavailable] = useState(false)

  latestRef.current = { arcs, airports, homeAirportIcao, selectedPairKey }
  onSelectRef.current = onSelectPair

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

    // Tile/style failures (typically no internet) must never throw or spam the console - an
    // unhandled 'error' listener is what makes MapLibre print to console itself.
    map.on('error', (_event: MapLibreErrorEvent) => setTilesUnavailable(true))

    const sync = () => {
      const current = mapRef.current
      if (!current) return
      const { arcs: currentArcs, airports: currentAirports, homeAirportIcao: hub, selectedPairKey: selected } = latestRef.current
      const colors = readMapColors()

      ensureLayers(current, colors.foreground)
      const source = current.getSource(LINK_SOURCE_ID) as GeoJSONSource | undefined
      if (source) {
        source.setData(buildFeatures(currentArcs))
        current.removeFeatureState({ source: LINK_SOURCE_ID })
        if (hoveredRef.current && hoveredRef.current !== selected) {
          current.setFeatureState({ source: LINK_SOURCE_ID, id: hoveredRef.current }, { emphasis: 'hovered' })
        }
        if (selected) {
          current.setFeatureState({ source: LINK_SOURCE_ID, id: selected }, { emphasis: 'selected' })
        }
      }

      // Markers are keyed by ICAO and only ever repositioned, so a re-render never rebuilds the
      // whole set - the same approach RouteMap takes.
      const wanted = new Set(currentAirports.map((a) => a.icao))
      for (const [icao, marker] of markersRef.current) {
        if (!wanted.has(icao)) {
          marker.remove()
          markersRef.current.delete(icao)
        }
      }
      for (const airport of currentAirports) {
        const existing = markersRef.current.get(airport.icao)
        if (existing) {
          existing.setLngLat([airport.longitude, airport.latitude])
          continue
        }
        const marker = new Marker({ element: airportMarkerElement(airport.icao, airport.icao === hub), anchor: 'bottom' })
          .setLngLat([airport.longitude, airport.latitude])
          .addTo(current)
        // Marker#addTo overwrites the element's aria-label with a generic "Map marker" - set the
        // real one after, not before, or it gets clobbered.
        marker.getElement().setAttribute('aria-label', airport.icao === hub ? `${airport.icao}, home base` : airport.icao)
        markersRef.current.set(airport.icao, marker)
      }

      // Fit once, to the whole network. Refitting on every data change would yank the viewport
      // around while the player is reading a route they just clicked.
      if (!hasFittedRef.current) {
        const points: LonLat[] = currentAirports.map((a) => [a.longitude, a.latitude])
        const bounds = points.length > 0 ? boundsForPath(points) : null
        if (bounds) {
          hasFittedRef.current = true
          current.fitBounds(bounds, { padding: { top: 64, bottom: 64, left: 64, right: 64 }, maxZoom: 7, duration: 0 })
        }
      }
    }

    const swapStyle = (nextMode: MapThemeMode) => {
      modeRef.current = nextMode
      styleReadyRef.current = false
      map.once('style.load', () => {
        styleReadyRef.current = true
        sync()
      })
      map.setStyle(buildMapStyle(nextMode, readMapColors()))
    }

    map.on('style.load', () => {
      if (!hasLoadedOnceRef.current) {
        hasLoadedOnceRef.current = true
        // The DOM's theme class can briefly disagree with the mode sampled synchronously above:
        // React runs a child's passive effects before its ancestors', so the theme provider may not
        // have applied the `dark` class yet. Self-correct once the first style has genuinely loaded
        // rather than racing a setStyle against a style that is still loading.
        const actualMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
        if (actualMode !== modeRef.current) {
          swapStyle(actualMode)
          return
        }
      }
      styleReadyRef.current = true
      sync()
    })

    map.on('mousemove', LINK_HIT_LAYER_ID, (event: MapLayerMouseEvent) => {
      const pairKey = event.features?.[0]?.properties?.pairKey as string | undefined
      map.getCanvas().style.cursor = 'pointer'
      if (!pairKey || pairKey === hoveredRef.current) return
      const previous = hoveredRef.current
      hoveredRef.current = pairKey
      if (previous && previous !== latestRef.current.selectedPairKey) {
        map.removeFeatureState({ source: LINK_SOURCE_ID, id: previous }, 'emphasis')
      }
      if (pairKey !== latestRef.current.selectedPairKey) {
        map.setFeatureState({ source: LINK_SOURCE_ID, id: pairKey }, { emphasis: 'hovered' })
      }
    })

    map.on('mouseleave', LINK_HIT_LAYER_ID, () => {
      map.getCanvas().style.cursor = ''
      const previous = hoveredRef.current
      hoveredRef.current = null
      if (previous && previous !== latestRef.current.selectedPairKey) {
        map.removeFeatureState({ source: LINK_SOURCE_ID, id: previous }, 'emphasis')
      }
    })

    map.on('click', LINK_HIT_LAYER_ID, (event: MapLayerMouseEvent) => {
      const pairKey = event.features?.[0]?.properties?.pairKey as string | undefined
      if (pairKey) onSelectRef.current(pairKey)
    })

    const observer = new MutationObserver(() => {
      const current = mapRef.current
      if (!current) return
      const nextMode: MapThemeMode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
      if (nextMode === modeRef.current) {
        // Same theme, but the accent (or a band token) may have been re-derived - repaint from the
        // current tokens without a full style swap.
        if (styleReadyRef.current) sync()
        return
      }
      if (!hasLoadedOnceRef.current) return
      swapStyle(nextMode)
    })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'style'] })

    return () => {
      observer.disconnect()
      for (const marker of markersRef.current.values()) marker.remove()
      markersRef.current.clear()
      map.remove()
      mapRef.current = null
      hasFittedRef.current = false
    }
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    const source = map.getSource(LINK_SOURCE_ID) as GeoJSONSource | undefined
    if (!source) return
    source.setData(buildFeatures(arcs))
    map.removeFeatureState({ source: LINK_SOURCE_ID })
    if (hoveredRef.current && hoveredRef.current !== selectedPairKey) {
      map.setFeatureState({ source: LINK_SOURCE_ID, id: hoveredRef.current }, { emphasis: 'hovered' })
    }
    if (selectedPairKey) {
      map.setFeatureState({ source: LINK_SOURCE_ID, id: selectedPairKey }, { emphasis: 'selected' })
    }
  }, [arcs, selectedPairKey])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !styleReadyRef.current) return
    const wanted = new Set(airports.map((a) => a.icao))
    for (const [icao, marker] of markersRef.current) {
      if (!wanted.has(icao)) {
        marker.remove()
        markersRef.current.delete(icao)
      }
    }
    for (const airport of airports) {
      const existing = markersRef.current.get(airport.icao)
      if (existing) {
        existing.setLngLat([airport.longitude, airport.latitude])
        continue
      }
      const marker = new Marker({
        element: airportMarkerElement(airport.icao, airport.icao === homeAirportIcao),
        anchor: 'bottom',
      })
        .setLngLat([airport.longitude, airport.latitude])
        .addTo(map)
      marker.getElement().setAttribute('aria-label', airport.icao === homeAirportIcao ? `${airport.icao}, home base` : airport.icao)
      markersRef.current.set(airport.icao, marker)
    }

    if (!hasFittedRef.current && airports.length > 0) {
      const bounds = boundsForPath(airports.map((a) => [a.longitude, a.latitude] as LonLat))
      if (bounds) {
        hasFittedRef.current = true
        map.fitBounds(bounds, { padding: { top: 64, bottom: 64, left: 64, right: 64 }, maxZoom: 7, duration: 0 })
      }
    }
  }, [airports, homeAirportIcao])

  return (
    <div className={cn('relative isolate overflow-hidden rounded-lg border border-border bg-surface', className)}>
      <div ref={containerRef} className="size-full min-h-[240px]" role="application" aria-label="Route network performance map" />
      {tilesUnavailable && (
        <div className="pointer-events-none absolute bottom-3 left-3 z-10 rounded-md border border-border bg-surface-elevated/90 px-2.5 py-1 text-xs text-muted-foreground shadow-elevation-2">
          Map tiles unavailable — showing offline view
        </div>
      )}
    </div>
  )
}

export default NetworkMap
