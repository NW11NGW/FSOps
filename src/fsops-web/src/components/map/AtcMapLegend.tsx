import { cn } from '@/lib/utils'
import type { VatsimAtcController } from '@/types/operations'
import { hasSectorCoverage, hasTerminalCoverage } from '@/components/map/atcGeometry'

/**
 * Legend for the ATC layer. Its whole job is to stop the map from lying by omission.
 *
 * On a map every shape looks equally authoritative, so a published FIR boundary and a circle drawn
 * from a controller's client setting are indistinguishable unless something says otherwise. This
 * says otherwise, in three parts:
 *
 *  - a solid outline is a **real published boundary**;
 *  - a dashed circle is an **approximate range**, not a controlled area;
 *  - a footnote names what is **not on the map at all** - approach TRACONs, which have no publicly
 *    bundleable geometry, and altitude limits, which the source data does not carry.
 *
 * The third part matters most and is the easiest to leave out. Without it a user reasonably reads
 * an empty area as "nobody is controlling there", when it may only mean "FSOps cannot say". Each
 * row only appears when that kind is actually drawn, so the legend never claims something the map
 * is not currently showing.
 */
export function AtcMapLegend({
  controllers,
  className,
}: {
  controllers: VatsimAtcController[]
  className?: string
}) {
  const sectors = hasSectorCoverage(controllers)
  const terminals = hasTerminalCoverage(controllers)
  if (!sectors && !terminals) return null

  return (
    <div className={cn('flex flex-col gap-1', className)}>
      {sectors && (
        <span className="flex items-center gap-1.5">
          <span
            aria-hidden="true"
            className="size-2.5 shrink-0 rounded-[2px] border border-warning bg-warning/15"
          />
          Sector · published boundary
        </span>
      )}
      {terminals && (
        <span className="flex items-center gap-1.5">
          <span
            aria-hidden="true"
            className="size-2.5 shrink-0 rounded-full border border-dashed border-warning bg-warning/10"
          />
          Terminal · approximate range
        </span>
      )}
      <span className="max-w-[15rem] text-pretty text-[10px] leading-snug text-muted-foreground/80">
        Lateral coverage only — no altitude limits. Approach TRACONs aren&rsquo;t shown.
      </span>
    </div>
  )
}
