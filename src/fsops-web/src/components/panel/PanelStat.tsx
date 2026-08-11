import type { ComponentType } from 'react'
import type { LucideProps } from 'lucide-react'

import { cn } from '@/lib/utils'

interface PanelStatProps {
  label: string
  value: string
  icon: ComponentType<LucideProps>
  valueClassName?: string
}

/**
 * A single glanceable readout - bigger type and denser padding than the dashboard's StatTile,
 * which is built for a wide grid rather than a toolbar window a few hundred pixels across.
 */
export function PanelStat({ label, value, icon: Icon, valueClassName }: PanelStatProps) {
  return (
    <div className="flex items-center gap-2.5 rounded-md border border-border bg-surface px-3 py-2.5">
      <Icon className="size-4 shrink-0 text-muted-foreground" />
      <div className="min-w-0">
        <p className="truncate text-[10px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
        <p className={cn('truncate font-mono text-lg font-semibold tabular-nums leading-tight', valueClassName)}>{value}</p>
      </div>
    </div>
  )
}
