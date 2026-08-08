import type { ComponentType } from 'react'
import type { LucideProps } from 'lucide-react'

import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

interface StatTileProps {
  label: string
  value?: string
  icon: ComponentType<LucideProps>
  trend?: { direction: 'up' | 'down' | 'flat'; label: string }
  loading?: boolean
}

const TREND_COLOR: Record<NonNullable<StatTileProps['trend']>['direction'], string> = {
  up: 'text-success',
  down: 'text-danger',
  flat: 'text-muted-foreground',
}

export function StatTile({ label, value, icon: Icon, trend, loading = false }: StatTileProps) {
  return (
    <Card>
      <CardContent className="space-y-2 p-5">
        {/* Icon and label share a row so the icon badge can never sit on top of wrapped label
         *  text - the label gets its own flex column (break-words so even a single long word like
         *  "DEVIATION" wraps instead of overflowing into the icon's column) and the icon keeps a
         *  fixed-size column of its own. Value and trend sit beneath, clear of both. */}
        <div className="flex items-start justify-between gap-3">
          <p className="min-w-0 break-words text-xs font-medium uppercase tracking-wide text-muted-foreground">
            {label}
          </p>
          <div className="flex size-9 shrink-0 items-center justify-center rounded-md bg-accent/15 text-accent">
            <Icon className="size-5" />
          </div>
        </div>
        {loading || value === undefined ? (
          <Skeleton className="h-7 w-24" />
        ) : (
          <p className="text-2xl font-semibold tabular-nums tracking-tight">{value}</p>
        )}
        {trend && !loading && (
          <p className={cn('text-xs font-medium', TREND_COLOR[trend.direction])}>{trend.label}</p>
        )}
        {loading && <Skeleton className="h-3 w-16" />}
      </CardContent>
    </Card>
  )
}
