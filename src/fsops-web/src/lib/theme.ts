interface Hsl {
  h: number
  s: number
  l: number
}

export function isValidHexColour(value: string): boolean {
  return /^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$/.test(value)
}

function hexToHsl(hex: string): Hsl {
  const normalized = hex.replace('#', '')
  const full = normalized.length === 3 ? normalized.split('').map((c) => c + c).join('') : normalized

  const r = parseInt(full.slice(0, 2), 16) / 255
  const g = parseInt(full.slice(2, 4), 16) / 255
  const b = parseInt(full.slice(4, 6), 16) / 255

  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2

  let h = 0
  let s = 0

  if (max !== min) {
    const d = max - min
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
    switch (max) {
      case r:
        h = (g - b) / d + (g < b ? 6 : 0)
        break
      case g:
        h = (b - r) / d + 2
        break
      default:
        h = (r - g) / d + 4
        break
    }
    h /= 6
  }

  return { h: Math.round(h * 360), s: Math.round(s * 100), l: Math.round(l * 100) }
}

/**
 * Applies an airline's accent colour to the live CSS custom properties that
 * drive Tailwind's `accent`/`ring` tokens (see index.css), so the whole app
 * chrome re-themes around the airline's brand colour without a reload.
 */
export function applyAccentColour(hex: string): void {
  if (typeof document === 'undefined' || !isValidHexColour(hex)) return
  const { h, s, l } = hexToHsl(hex)
  const root = document.documentElement
  root.style.setProperty('--accent', `${h} ${s}% ${l}%`)
  root.style.setProperty('--ring', `${h} ${s}% ${l}%`)
  // Light accents need dark text on top; everything else reads fine in near-white text.
  root.style.setProperty('--accent-foreground', l > 70 ? '222 47% 11%' : '210 40% 98%')
}
