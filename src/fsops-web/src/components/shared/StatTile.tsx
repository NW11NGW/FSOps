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
      <CardContent className="flex items-start justify-between gap-4 p-5">
        <div className="min-w-0 space-y-2">
          <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
          {loading || value === undefined ? (
            <Skeleton className="h-7 w-24" />
          ) : (
            <p className="text-2xl font-semibold tabular-nums tracking-tight">{value}</p>
          )}
          {trend && !loading && (
            <p className={cn('text-xs font-medium', TREND_COLOR[trend.direction])}>{trend.label}</p>
          )}
          {loading && <Skeleton className="h-3 w-16" />}
        </div>
        <div className="flex size-9 shrink-0 items-center justify-center rounded-md bg-accent/15 text-accent">
          <Icon className="size-5" />
        </div>
      </CardContent>
    </Card>
  )
}
