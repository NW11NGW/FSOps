import type { StyleSpecification } from 'maplibre-gl'

export type MapThemeMode = 'dark' | 'light'

export interface MapColors {
  accent: string
  accentGlow: string
  background: string
  border: string
  foreground: string
  mutedForeground: string
}

/**
 * MapLibre paint properties need literal CSS colour strings, not `var(...)` references, so the
 * design tokens (raw "H S% L%" triplets - see index.css) are read from the live computed style
 * and wrapped in hsl(...) at call time. This keeps the map in sync with both the light/dark
 * theme and the airline's accent colour without hardcoding any hex values here.
 */
export function readMapColors(): MapColors {
  const style = typeof window === 'undefined' ? null : getComputedStyle(document.documentElement)
  const read = (name: string, fallback: string) => style?.getPropertyValue(name).trim() || fallback

  const accent = read('--accent', '199 89% 48%')
  const background = read('--background', '222 47% 6%')
  const border = read('--border', '217 33% 18%')
  const foreground = read('--foreground', '210 40% 96%')
  const mutedForeground = read('--muted-foreground', '215 20% 65%')

  return {
    accent: `hsl(${accent})`,
    accentGlow: `hsl(${accent} / 0.5)`,
    background: `hsl(${background})`,
    border: `hsl(${border})`,
    foreground: `hsl(${foreground})`,
    mutedForeground: `hsl(${mutedForeground})`,
  }
}

const CARTO_SUBDOMAINS = ['a', 'b', 'c', 'd']

function cartoTiles(variant: 'dark_all' | 'light_all'): string[] {
  return CARTO_SUBDOMAINS.map((s) => `https://${s}.basemaps.cartocdn.com/${variant}/{z}/{x}/{y}{r}.png`)
}

const CARTO_ATTRIBUTION =
  '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/attributions" target="_blank" rel="noreferrer">CARTO</a>'

/**
 * Basemap style. The `background` layer sits underneath the raster tiles and is always themed
 * from design tokens, so if the CARTO tiles fail to load (no internet), the map still reads as
 * an intentional, themed surface rather than a blank/broken rectangle - route geometry and
 * airport markers stay fully visible either way.
 */
export function buildMapStyle(mode: MapThemeMode, colors: MapColors): StyleSpecification {
  return {
    version: 8,
    glyphs: 'https://fonts.openmaptiles.org/{fontstack}/{range}.pbf',
    sources: {
      carto: {
        type: 'raster',
        tiles: cartoTiles(mode === 'dark' ? 'dark_all' : 'light_all'),
        tileSize: 256,
        maxzoom: 20,
        attribution: CARTO_ATTRIBUTION,
      },
    },
    layers: [
      {
        id: 'background',
        type: 'background',
        paint: { 'background-color': colors.background },
      },
      {
        id: 'carto-basemap',
        type: 'raster',
        source: 'carto',
        paint: { 'raster-opacity': mode === 'dark' ? 0.92 : 1 },
      },
    ],
  }
}
