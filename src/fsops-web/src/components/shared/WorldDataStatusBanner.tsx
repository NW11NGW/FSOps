import { AlertTriangle, Loader2 } from 'lucide-react'

import { Card, CardContent } from '@/components/ui/card'
import type { WorldDataFetchStatus } from '@/hooks/useWorldDataStatus'
import type { WorldDataStatus } from '@/types/airport'

interface WorldDataStatusBannerProps {
  status: WorldDataFetchStatus
  data: WorldDataStatus | null
}

/**
 * Shows up only when there's something to say: an import running (first run
 * seeds ~78,000 airports), or a database that hasn't been seeded yet with no
 * import underway. Once seeded and idle, renders nothing.
 */
export function WorldDataStatusBanner({ status, data }: WorldDataStatusBannerProps) {
  if (status === 'loading' || !data) return null
  if (!data.importInProgress && data.seeded) return null

  if (data.importInProgress) {
    const percent = Math.min(100, Math.max(0, Math.round(data.progressPercent)))
    return (
      <Card className="mb-4 border-accent/30 bg-accent/5">
        <CardContent className="flex flex-wrap items-center gap-4 p-4">
          <Loader2 className="size-5 shrink-0 animate-spin text-accent" />
          <div className="min-w-0 flex-1">
            <p className="text-sm font-medium">Importing world airport data…</p>
            <p className="text-xs text-muted-foreground">
              {data.airportCount.toLocaleString()} airports imported so far — this only happens once.
            </p>
          </div>
          <div className="flex items-center gap-2">
            <div className="h-1.5 w-32 overflow-hidden rounded-full bg-muted">
              <div className="h-full rounded-full bg-accent transition-[width]" style={{ width: `${percent}%` }} />
            </div>
            <span className="text-xs font-medium tabular-nums text-muted-foreground">{percent}%</span>
          </div>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="mb-4 border-warning/30 bg-warning/5">
      <CardContent className="flex items-center gap-3 p-4">
        <AlertTriangle className="size-5 shrink-0 text-warning" />
        <p className="text-sm">
          World airport data hasn&apos;t been imported yet. Airport search and route planning will be unavailable
          until the import runs.
        </p>
      </CardContent>
    </Card>
  )
}
