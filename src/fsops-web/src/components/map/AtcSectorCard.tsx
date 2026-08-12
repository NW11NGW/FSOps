import { useLayoutEffect, useRef, useState } from 'react'
import { Radio } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import type { VatsimAtcController } from '@/types/operations'

interface AtcSectorCardProps {
  boundaryName: string
  /** Every controller currently working this region - more than one when a sector is split
   *  between positions whose internal division FSOps has no data for. */
  controllers: VatsimAtcController[]
  /** Viewport-relative pixel coordinates of the cursor, same convention as LiveFlightCard: this
   *  renders `position: fixed` so it escapes the map's `overflow-hidden` container. A sector has
   *  no marker to anchor to - it is an area - so it follows the pointer instead. */
  x: number
  y: number
}

/**
 * Hover card for an en-route sector. Deliberately says what the polygon does and does not mean:
 * a published lateral boundary, no altitude limits. Where two controllers share the region they
 * are both listed under one shape rather than implying two separately-bounded sectors.
 */
export function AtcSectorCard({ boundaryName, controllers, x, y }: AtcSectorCardProps) {
  const cardRef = useRef<HTMLDivElement>(null)
  const [style, setStyle] = useState<{ left: number; top: number }>({ left: x, top: y })

  useLayoutEffect(() => {
    const el = cardRef.current
    if (!el) {
      setStyle({ left: x, top: y })
      return
    }
    const { width, height } = el.getBoundingClientRect()
    const margin = 8
    let left = x - width / 2
    let top = y - height - 16
    left = Math.min(Math.max(left, margin), window.innerWidth - width - margin)
    if (top < margin) top = y + 16 // flip below the cursor if there's no room above
    top = Math.min(top, window.innerHeight - height - margin)
    setStyle({ left, top })
  }, [x, y])

  return (
    <div
      ref={cardRef}
      className="pointer-events-none fixed z-50 w-64 rounded-lg border border-border bg-surface-elevated p-3 shadow-elevation-3"
      style={{ left: style.left, top: style.top }}
      role="status"
    >
      <div className="flex items-start justify-between gap-2">
        <p className="min-w-0 break-words text-sm font-semibold">{boundaryName}</p>
        <Badge variant="outline" className="shrink-0">
          Sector
        </Badge>
      </div>

      <ul className="mt-2 space-y-1.5">
        {controllers.map((controller) => (
          <li key={controller.callsign} className="flex items-center justify-between gap-2 text-xs">
            <span className="min-w-0 truncate font-mono font-semibold tabular-nums">{controller.callsign}</span>
            <span className="flex shrink-0 items-center gap-1 tabular-nums text-muted-foreground">
              <Radio className="size-3 shrink-0" />
              {controller.frequency}
            </span>
          </li>
        ))}
      </ul>

      <p className="mt-2.5 border-t border-border pt-2 text-[10px] leading-snug text-muted-foreground">
        Published lateral boundary. FSOps has no altitude limits for it, so this says nothing about
        which levels are covered.
      </p>
    </div>
  )
}
