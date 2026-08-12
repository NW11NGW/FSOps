/**
 * Deterministic colour assignment for the schedule overview's by-aircraft view, where every
 * aircraft is a row and its legs are colour-coded by pilot. Six categorical hues (the
 * `--chart-*` tokens in index.css, tuned for both themes) cycled by a stable hash of the pilot id
 * - not by list position, so a given pilot keeps the same colour across renders and toggles
 * between the by-pilot and by-aircraft views, even as other pilots are hired or released.
 */
const CHART_TOKEN_COUNT = 6

const SWATCH_CLASSES = [
  { dot: 'bg-chart-1', text: 'text-chart-1', chip: 'bg-chart-1/15 text-chart-1 border-chart-1/30' },
  { dot: 'bg-chart-2', text: 'text-chart-2', chip: 'bg-chart-2/15 text-chart-2 border-chart-2/30' },
  { dot: 'bg-chart-3', text: 'text-chart-3', chip: 'bg-chart-3/15 text-chart-3 border-chart-3/30' },
  { dot: 'bg-chart-4', text: 'text-chart-4', chip: 'bg-chart-4/15 text-chart-4 border-chart-4/30' },
  { dot: 'bg-chart-5', text: 'text-chart-5', chip: 'bg-chart-5/15 text-chart-5 border-chart-5/30' },
  { dot: 'bg-chart-6', text: 'text-chart-6', chip: 'bg-chart-6/15 text-chart-6 border-chart-6/30' },
] as const

export interface PilotSwatch {
  dot: string
  text: string
  chip: string
}

function hashString(value: string): number {
  let hash = 0
  for (let i = 0; i < value.length; i += 1) {
    hash = (hash * 31 + value.charCodeAt(i)) | 0
  }
  return Math.abs(hash)
}

export function pilotSwatch(pilotId: string): PilotSwatch {
  const index = hashString(pilotId) % CHART_TOKEN_COUNT
  return SWATCH_CLASSES[index] ?? SWATCH_CLASSES[0]!
}
