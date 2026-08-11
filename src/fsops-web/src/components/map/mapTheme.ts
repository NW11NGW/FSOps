import type { StyleSpecification } from 'maplibre-gl'

export type MapThemeMode = 'dark' | 'light'

export interface MapColors {
  accent: string
  accentGlow: string
  background: string
  border: string
  foreground: string
  mutedForeground: string
  /** ATC coverage colour - deliberately distinct from `accent` (which the route network and the
   *  player's own aircraft already use) so controller sector shading never reads as "more
   *  network" at a glance. Legible against both the light and dark CARTO basemaps. */
  atc: string
  atcFill: string
}

/**
 * Converts a design token ("H S% L%", the space-separated form Tailwind expects) into an
 * rgb()/rgba() string.
 *
 * MapLibre parses paint colours with its own parser rather than the browser's, and it does not
 * accept the space-separated hsl() syntax that CSS Color 4 introduced. Handing it
 * `hsl(199 89% 48%)` leaves the layer unpainted with no error - the source and layers exist and
 * report the right feature count, but nothing appears. Emitting rgb() sidesteps the whole issue
 * because every parser understands it.
 */
function tokenToRgb(triplet: string, alpha = 1): string {
  const [h, s, l] = triplet
    .split(/[\s,]+/)
    .map((part) => Number.parseFloat(part.replace('%', '')))

  if (![h, s, l].every((n) => Number.isFinite(n))) {
    // Unrecognised token - fall back to something visible rather than an unpaintable colour.
    return alpha < 1 ? `rgba(56, 189, 248, ${alpha})` : 'rgb(56, 189, 248)'
  }

  const saturation = s! / 100
  const lightness = l! / 100
  const chroma = (1 - Math.abs(2 * lightness - 1)) * saturation
  const hue = ((h! % 360) + 360) % 360
  const second = chroma * (1 - Math.abs(((hue / 60) % 2) - 1))
  const match = lightness - chroma / 2

  const [r, g, b] = (
    hue < 60 ? [chroma, second, 0]
      : hue < 120 ? [second, chroma, 0]
        : hue < 180 ? [0, chroma, second]
          : hue < 240 ? [0, second, chroma]
            : hue < 300 ? [second, 0, chroma]
              : [chroma, 0, second]
  ).map((channel) => Math.round((channel + match) * 255))

  return alpha < 1 ? `rgba(${r}, ${g}, ${b}, ${alpha})` : `rgb(${r}, ${g}, ${b})`
}

/**
 * MapLibre paint properties need literal colour strings, not `var(...)` references, so the design
 * tokens are read from the live computed style and converted at call time. This keeps the map in
 * sync with both the light/dark theme and the airline's accent colour without hardcoding colours.
 */
export function readMapColors(): MapColors {
  const style = typeof window === 'undefined' ? null : getComputedStyle(document.documentElement)
  const read = (name: string, fallback: string) => style?.getPropertyValue(name).trim() || fallback

  const accent = read('--accent', '199 89% 48%')
  const background = read('--background', '222 47% 6%')
  const border = read('--border', '217 33% 18%')
  const foreground = read('--foreground', '210 40% 96%')
  const mutedForeground = read('--muted-foreground', '215 20% 65%')
  const warning = read('--warning', '38 92% 50%')

  return {
    accent: tokenToRgb(accent),
    accentGlow: tokenToRgb(accent, 0.5),
    background: tokenToRgb(background),
    border: tokenToRgb(border),
    foreground: tokenToRgb(foreground),
    mutedForeground: tokenToRgb(mutedForeground),
    atc: tokenToRgb(warning),
    atcFill: tokenToRgb(warning, 0.12),
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
